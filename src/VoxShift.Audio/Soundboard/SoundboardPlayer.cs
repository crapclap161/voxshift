using System.Collections.Concurrent;
using VoxShift.Core.Audio;
using VoxShift.Core.Logging;

namespace VoxShift.Audio.Soundboard;

public enum PlayStatus
{
    Started,
    Missing,
    Failed,
}

public readonly record struct PlayResult(PlayStatus Status, string? Message = null)
{
    public static readonly PlayResult Ok = new(PlayStatus.Started);
}

/// <summary>
/// Plays soundboard files into the virtual microphone (the router's <see cref="SoundboardMixer"/>, heard in VRChat)
/// and, when monitoring is on, into your own headphones (<see cref="MonitorMix"/>). Each target only receives
/// the sound while its output is running, so sounds never pile up and play late after a device comes back.
/// Decoding and resampling happen on thread-pool threads and are cached (bounded by memory), so a click only
/// hands an already prepared clip to the mixer. The render thread never touches the disk.
/// </summary>
public sealed class SoundboardPlayer : IDisposable
{
    /// <summary>Upper bound for decoded audio kept in memory across all cached sounds.</summary>
    public const long CacheBudgetBytes = 256L * 1024 * 1024;

    private readonly AudioRouter _router;
    private readonly MonitorMix _monitor;
    private readonly Func<bool> _cableActive;
    private readonly Func<bool> _monitorActive;
    private readonly ILog _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<AudioClip>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastUsed = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public SoundboardPlayer(AudioRouter router, MonitorMix monitor, Func<bool> cableActive, Func<bool> monitorActive, ILog log)
    {
        _router = router;
        _monitor = monitor;
        _cableActive = cableActive;
        _monitorActive = monitorActive;
        _log = log;
    }

    public SoundboardMixer Mixer => _router.Soundboard;

    /// <summary>
    /// Decodes (if needed) and starts the sound on every running target: VRChat (virtual cable) and, if enabled,
    /// your headphones. Safe to call from the UI thread; never blocks it.
    /// </summary>
    /// <param name="ignoreOutputState">Diagnostics only: play into the mixers even if no device is running.</param>
    public async Task<PlayResult> PlayAsync(string soundId, string path, bool ignoreOutputState = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path)) return new PlayResult(PlayStatus.Missing, $"File not found: {path}");

        bool toCable = ignoreOutputState || _cableActive();
        bool toMonitor = !ignoreOutputState && _monitorActive();
        if (!toCable && !toMonitor)
        {
            return new PlayResult(PlayStatus.Failed, "No audio output is running: neither the virtual microphone nor your headphones.");
        }

        try
        {
            if (toCable)
            {
                var clip = await GetClipForAsync(path, _router.Soundboard).ConfigureAwait(false);
                _router.Soundboard.Play(soundId, clip);
            }

            if (toMonitor && _monitorActive())
            {
                var clip = await GetClipForAsync(path, _monitor.Mixer).ConfigureAwait(false);
                _monitor.Mixer.Play(soundId, clip);
            }

            return PlayResult.Ok;
        }
        catch (FileNotFoundException)
        {
            return new PlayResult(PlayStatus.Missing, $"File not found: {path}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Soundboard could not play \"{path}\".", ex);
            return new PlayResult(PlayStatus.Failed, ex is AudioCaptureException ? ex.Message : $"Could not play \"{Path.GetFileName(path)}\": {ex.Message}");
        }
    }

    /// <summary>Decodes sounds in the background so the first click starts instantly.</summary>
    public void Preload(IEnumerable<string> paths)
    {
        if (_disposed) return;
        var rates = new HashSet<int> { _router.OutputRate };
        if (_monitorActive()) rates.Add(_monitor.OutputRate);
        foreach (var (path, rate) in paths.Where(File.Exists).Take(64).SelectMany(p => rates.Select(r => (p, r))))
        {
            _ = GetClipAsync(path, rate).ContinueWith(
                t => _log.Debug($"Preload failed for {path}: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    public void Stop(string soundId)
    {
        _router.Soundboard.Stop(soundId);
        _monitor.Mixer.Stop(soundId);
    }

    public void StopAll()
    {
        _router.Soundboard.StopAll();
        _monitor.Mixer.StopAll();
    }

    /// <summary>Gets the clip at the mixer's current rate (re-fetches if the device rate changed while decoding).</summary>
    private async Task<AudioClip> GetClipForAsync(string path, SoundboardMixer mixer)
    {
        var clip = await GetClipAsync(path, mixer.OutputRate).ConfigureAwait(false);
        if (clip.SampleRate != mixer.OutputRate) clip = await GetClipAsync(path, mixer.OutputRate).ConfigureAwait(false);
        return clip;
    }

    /// <summary>Forgets cached audio of a file (e.g. after relinking).</summary>
    public void Invalidate(string path)
    {
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _cache.TryRemove(key, out _);
            _lastUsed.TryRemove(key, out _);
        }
    }

    private Task<AudioClip> GetClipAsync(string path, int rate)
    {
        var info = new FileInfo(path);
        string key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{rate}";
        _lastUsed[key] = Environment.TickCount64;
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<AudioClip>>(() => Task.Run(() => LoadClip(path, rate))));
        var task = lazy.Value;
        if (task.IsFaulted) _cache.TryRemove(key, out _); // retry next time (file may have been fixed)
        return task;
    }

    private AudioClip LoadClip(string path, int rate)
    {
        var decoded = AudioFileDecoder.Decode(path);
        var clip = decoded.ResampleTo(rate);
        _log.Debug($"Soundboard loaded \"{Path.GetFileName(path)}\" ({clip.Duration.TotalSeconds:0.0} s, {decoded.SampleRate} → {rate} Hz).");
        TrimCache();
        return clip;
    }

    private void TrimCache()
    {
        long total = 0;
        var entries = new List<(string Key, long Size, long Used)>();
        foreach (var (key, lazy) in _cache)
        {
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully) continue;
            long size = lazy.Value.Result.SizeInBytes;
            total += size;
            entries.Add((key, size, _lastUsed.TryGetValue(key, out long u) ? u : 0));
        }

        foreach (var e in entries.OrderBy(e => e.Used))
        {
            if (total <= CacheBudgetBytes) break;
            if (_cache.TryRemove(e.Key, out _))
            {
                _lastUsed.TryRemove(e.Key, out _);
                total -= e.Size; // playing voices keep their own reference, so eviction never cuts a sound
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.Soundboard.Clear();
        _monitor.Mixer.Clear();
        _cache.Clear();
        _lastUsed.Clear();
    }
}
