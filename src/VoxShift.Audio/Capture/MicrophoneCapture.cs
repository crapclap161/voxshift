using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoxShift.Core.Audio;
using VoxShift.Core.Logging;

namespace VoxShift.Audio.Capture;

/// <summary>
/// Shared-mode, event-driven WASAPI capture of a physical microphone. Shared mode means other programs
/// (Discord, Windows voice typing, …) can keep using the same microphone at the same time; nothing is muted
/// or disabled system-wide.
/// </summary>
public sealed class MicrophoneCapture : ICaptureSource
{
    private readonly MMDevice _device;
    private readonly StereoDataHandler _onData;
    private readonly ILog _log;
    private WasapiCapture? _capture;
    private float[] _stereo = new float[48000 * 2];
    private StreamFormat _format;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <param name="device">Capture endpoint; ownership stays with the caller.</param>
    public MicrophoneCapture(MMDevice device, StereoDataHandler onData, ILog log)
    {
        _device = device;
        _onData = onData;
        _log = log;
        DeviceId = device.ID;
        DeviceName = SafeName(device);
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public StreamFormat? Format { get; private set; }

    public bool IsRunning { get; private set; }

    public event Action<ICaptureSource, Exception?>? Stopped;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture is not null) throw new InvalidOperationException("Capture already started.");

        try
        {
            var capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 30);
            _format = WaveFormatMapper.ToStreamFormat(capture.WaveFormat);
            Format = _format;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            _stopRequested = false;
            capture.StartRecording();
            IsRunning = true;
            _log.Info($"Microphone capture started: {DeviceName} ({_format}).");
        }
        catch (Exception ex) when (ex is not AudioCaptureException)
        {
            CleanupCapture();
            throw new AudioCaptureException($"Could not open microphone \"{DeviceName}\": {Describe(ex)}", ex.HResult, ex);
        }
        catch
        {
            CleanupCapture();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            int frames = e.BytesRecorded / _format.BlockAlign;
            if (frames <= 0) return;
            if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
            SampleConverter.ToStereoFloat(e.Buffer.AsSpan(0, frames * _format.BlockAlign), _format, frames, _stereo);
            _onData(_stereo.AsSpan(0, frames * 2), frames, _format.SampleRate);
        }
        catch (Exception ex)
        {
            // Never let an exception escape into NAudio's capture thread.
            _log.Error("Error processing microphone data.", ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;
        if (e.Exception is not null)
        {
            _log.Warn($"Microphone capture stopped unexpectedly: {Describe(e.Exception)}");
        }

        Stopped?.Invoke(this, _stopRequested ? null : e.Exception ?? new AudioCaptureException("Microphone capture stopped."));
    }

    private static string Describe(Exception ex) => ex.HResult switch
    {
        unchecked((int)0x88890004) => "the microphone was unplugged or disabled.",
        unchecked((int)0x8889000A) => "the microphone is in use in exclusive mode by another application.",
        unchecked((int)0x80070005) => "access was denied. Check Windows Settings → Privacy & security → Microphone and allow desktop apps to access the microphone.",
        _ => ex.Message,
    };

    private static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch (Exception) { return "microphone"; }
    }

    private void CleanupCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.Dispose(); }
        catch (Exception ex) { _log.Warn("Error disposing microphone capture.", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRequested = true;
        try { _capture?.StopRecording(); }
        catch (Exception ex) { _log.Warn("Error stopping microphone capture.", ex); }
        CleanupCapture();
        IsRunning = false;
    }
}
