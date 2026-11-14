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
    private readonly ClipCache _cache = new(CacheBudgetBytes);
    private volatile bool _disposed;

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
        if (_disposed) return new PlayResult(PlayStatus.Failed, "VoxShift is shutting down.");
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
            try
            {
                _ = GetClipAsync(path, rate).ContinueWith(
                    t => _log.Debug($"Preload failed for {path}: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file vanished or is unreadable right now; playing it later reports the problem to the user.
                _log.Debug($"Preload skipped for {path}: {ex.Message}");
            }
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

    /// <summary>
    /// Playback progress of every sound for the UI. Follows what VRChat hears; while the virtual cable is not running
    /// and sounds only play in your headphones, it follows the monitor instead, so a playing button still shows (and
    /// can be stopped) as playing.
    /// </summary>
    public void CopyProgress(ICollection<VoiceProgress> target)
    {
        _router.Soundboard.CopyProgress(target);
        if (target.Count == 0) _monitor.Mixer.CopyProgress(target);
    }

    /// <summary>Forgets cached audio of a file (e.g. after relinking).</summary>
    public void Invalidate(string path) => _cache.Invalidate(path + "|");

    private Task<AudioClip> GetClipAsync(string path, int rate)
    {
        // Length and write time are part of the key, so an edited or replaced file is decoded again.
        var info = new FileInfo(path);
        string key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{rate}";
        return _cache.GetOrAddAsync(key, () => LoadClip(path, rate));
    }

    private AudioClip LoadClip(string path, int rate)
    {
        var decoded = AudioFileDecoder.Decode(path);
        var clip = decoded.ResampleTo(rate);
        _log.Debug($"Soundboard loaded \"{Path.GetFileName(path)}\" ({clip.Duration.TotalSeconds:0.0} s, {decoded.SampleRate} → {rate} Hz).");
        return clip;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.Soundboard.Clear();
        _monitor.Mixer.Clear();
        _cache.Clear();
    }
}
