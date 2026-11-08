using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoxShift.Core.Audio;
using VoxShift.Core.Logging;

namespace VoxShift.Audio.Output;

/// <summary>
/// Shared-mode, event-driven WASAPI playback of an <see cref="IStereoRenderSource"/>. Used twice:
/// the router into the virtual cable ("CABLE Input", which VRChat records as its microphone), and the soundboard
/// monitor mix into your own headphones. The output format follows the device's mix format, so Windows never
/// has to convert.
/// </summary>
public sealed class StereoRenderOutput : IDisposable
{
    private const int LatencyMs = 30;

    private readonly MMDevice _device;
    private readonly IStereoRenderSource _source;
    private readonly ILog _log;
    private WasapiOut? _output;
    private volatile bool _stopRequested;
    private bool _disposed;

    public StereoRenderOutput(MMDevice device, IStereoRenderSource source, ILog log)
    {
        _device = device;
        _source = source;
        _log = log;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public bool IsRunning { get; private set; }

    public event Action<StereoRenderOutput, Exception?>? Stopped;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            WaveFormat mix;
            using (var probe = _device.AudioClient)
            {
                mix = probe.MixFormat;
            }

            SampleRate = mix.SampleRate;
            Channels = Math.Clamp(mix.Channels, 1, 8);
            _source.SetOutputRate(SampleRate);

            var provider = new SourceWaveProvider(_source, SampleRate, Channels);
            var output = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency: LatencyMs);
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(provider);
            _output = output;
            _stopRequested = false;
            output.Play();
            IsRunning = true;
            _log.Info($"Output started: {DeviceName} ({SampleRate} Hz, {Channels} ch).");
        }
        catch (Exception ex)
        {
            Cleanup();
            throw new AudioCaptureException($"Could not open \"{DeviceName}\": {ex.Message}", ex.HResult, ex);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;
        if (e.Exception is not null) _log.Warn($"Output stopped unexpectedly: {e.Exception.Message}");
        Stopped?.Invoke(this, _stopRequested ? null : e.Exception ?? new AudioCaptureException("Output stopped."));
    }

    private void Cleanup()
    {
        var output = _output;
        _output = null;
        if (output is null) return;
        output.PlaybackStopped -= OnPlaybackStopped;
        try { output.Dispose(); }
        catch (Exception ex) { _log.Warn("Error disposing output.", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRequested = true;
        try { _output?.Stop(); }
        catch (Exception ex) { _log.Warn("Error stopping output.", ex); }
        Cleanup();
        IsRunning = false;
    }

    /// <summary>
    /// Pull adapter: WASAPI asks for bytes, the source renders stereo float which is mapped to the
    /// device's channel count. Always returns the full requested amount (silence if needed) so playback
    /// never stops on its own.
    /// </summary>
    private sealed class SourceWaveProvider : IWaveProvider
    {
        private readonly IStereoRenderSource _source;
        private readonly int _channels;
        private float[] _stereo = new float[4096 * 2];
        private float[] _mapped = new float[4096 * 8];

        public SourceWaveProvider(IStereoRenderSource source, int sampleRate, int channels)
        {
            _source = source;
            _channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / (4 * _channels);
            if (frames <= 0) return count;
            if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
            if (_mapped.Length < frames * _channels) _mapped = new float[frames * _channels];

            try
            {
                _source.Render(_stereo, frames);
            }
            catch (Exception)
            {
                Array.Clear(_stereo, 0, frames * 2);
            }

            int bytes = frames * _channels * 4;
            if (_channels == 2)
            {
                Buffer.BlockCopy(_stereo, 0, buffer, offset, bytes);
            }
            else
            {
                SampleConverter.StereoToChannels(_stereo, frames, _mapped, _channels);
                Buffer.BlockCopy(_mapped, 0, buffer, offset, bytes);
            }

            // Zero any trailing partial frame.
            if (bytes < count) Array.Clear(buffer, offset + bytes, count - bytes);
            return count;
        }
    }
}
