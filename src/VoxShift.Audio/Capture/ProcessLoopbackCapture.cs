using System.Runtime.InteropServices;
using VoxShift.Audio.Interop;
using VoxShift.Core.Audio;
using VoxShift.Core.Logging;

namespace VoxShift.Audio.Capture;

/// <summary>
/// Captures the audio of one application (process tree) using Windows' process loopback API.
/// Unlike regular loopback capture this does <b>not</b> record the whole desktop mix: only audio
/// rendered by the target process (and its children) is delivered. The application keeps playing to
/// the user's headphones normally – loopback capture is a passive copy.
/// </summary>
public sealed class ProcessLoopbackCapture : ICaptureSource
{
    /// <summary>Process loopback is available starting with Windows 10 build 20348 (Windows 11 = 22000+).</summary>
    public const int MinimumWindowsBuild = 20348;

    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const long BufferDuration100ns = 1_000_000; // 100 ms ring inside the audio engine

    private readonly int _processId;
    private readonly bool _includeTree;
    private readonly StereoDataHandler _onData;
    private readonly ILog _log;
    private readonly ManualResetEvent _stopEvent = new(false);
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    public ProcessLoopbackCapture(int processId, bool includeProcessTree, StereoDataHandler onData, ILog log)
    {
        _processId = processId;
        _includeTree = includeProcessTree;
        _onData = onData;
        _log = log;
    }

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild);

    public int ProcessId => _processId;

    public StreamFormat? Format { get; private set; }

    public bool IsRunning => _running;

    public event Action<ICaptureSource, Exception?>? Stopped;

    /// <summary>
    /// Starts capturing. Activation happens synchronously on the capture thread so that failures
    /// (unsupported OS, access denied …) are thrown from here instead of failing silently.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) throw new InvalidOperationException("Capture already started.");
        if (!IsSupported)
        {
            throw new AudioCaptureException(
                $"Application audio capture needs Windows 11 or Windows 10 build {MinimumWindowsBuild}+ (this is build {Environment.OSVersion.Version.Build}).");
        }

        var started = new ManualResetEventSlim(false);
        Exception? startError = null;
        _stopEvent.Reset();
        _thread = new Thread(() => CaptureThread(started, e => startError = e))
        {
            IsBackground = true,
            Name = $"Process loopback capture ({_processId})",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        started.Wait();
        if (startError is not null)
        {
            _thread.Join(1000);
            _thread = null;
            throw startError is AudioCaptureException ? startError : new AudioCaptureException(startError.Message, startError.HResult, startError);
        }
    }

    private void CaptureThread(ManualResetEventSlim started, Action<Exception> reportStartError)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? captureClient = null;
        var audioEvent = new AutoResetEvent(false);
        IntPtr mmcss = IntPtr.Zero;
        Exception? failure = null;
        bool startedSignalled = false;
        bool captureStarted = false;

        try
        {
            client = ActivateAndInitialize();

            int hr = client.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle());
            Check(hr, "SetEventHandle");

            hr = client.GetService(NativeMethods.IID_IAudioCaptureClient, out object service);
            Check(hr, "GetService(IAudioCaptureClient)");
            captureClient = (IAudioCaptureClient)service;

            hr = client.Start();
            Check(hr, "Start");

            _running = true;
            captureStarted = true;
            startedSignalled = true;
            started.Set();
            _log.Info($"Application capture started for PID {_processId} ({Format}).");

            uint taskIndex = 0;
            mmcss = NativeMethods.AvSetMmThreadCharacteristics("Audio", ref taskIndex);

            CaptureLoop(captureClient, audioEvent);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (!startedSignalled)
            {
                reportStartError(ex);
                started.Set();
                startedSignalled = true;
            }
            else
            {
                _log.Error($"Application capture for PID {_processId} failed.", ex);
            }
        }
        finally
        {
            _running = false;
            try { client?.Stop(); } catch (COMException) { }
            if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            if (captureClient is not null) Marshal.ReleaseComObject(captureClient);
            if (client is not null) Marshal.ReleaseComObject(client);
            audioEvent.Dispose();
            if (!startedSignalled) started.Set();
        }

        if (captureStarted)
        {
            Stopped?.Invoke(this, failure);
        }
    }

    /// <summary>
    /// Activates a process loopback client and initialises it. Each attempt uses a fresh client because an
    /// IAudioClient whose Initialize failed must not be reused. Preference order: 32-bit float, then 16-bit PCM
    /// (the format of Microsoft's sample); each first with, then without, the auto-conversion flags.
    /// </summary>
    private IAudioClient ActivateAndInitialize()
    {
        const AudioClientStreamFlags baseFlags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback;
        const AudioClientStreamFlags convertFlags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
        var attempts = new (WaveFormatEx Wfx, StreamFormat Desc, AudioClientStreamFlags Flags)[]
        {
            (WaveFormatEx.Create(WaveFormatEx.WAVE_FORMAT_IEEE_FLOAT, SampleRate, Channels, 32), new StreamFormat(SampleFormat.Float32, Channels, SampleRate), baseFlags | convertFlags),
            (WaveFormatEx.Create(WaveFormatEx.WAVE_FORMAT_PCM, SampleRate, Channels, 16), new StreamFormat(SampleFormat.Pcm16, Channels, SampleRate), baseFlags | convertFlags),
            (WaveFormatEx.Create(WaveFormatEx.WAVE_FORMAT_IEEE_FLOAT, SampleRate, Channels, 32), new StreamFormat(SampleFormat.Float32, Channels, SampleRate), baseFlags),
            (WaveFormatEx.Create(WaveFormatEx.WAVE_FORMAT_PCM, SampleRate, Channels, 16), new StreamFormat(SampleFormat.Pcm16, Channels, SampleRate), baseFlags),
        };

        int lastHr = 0;
        foreach (var (wfx, desc, flags) in attempts)
        {
            // Activation errors (unsupported OS, access denied) are not format related: let them propagate.
            IAudioClient client = ProcessLoopbackActivator.Activate(_processId, _includeTree, TimeSpan.FromSeconds(5));
            IntPtr pFormat = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                Marshal.StructureToPtr(wfx, pFormat, false);
                lastHr = client.Initialize(NativeMethods.AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration100ns, 0, pFormat, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pFormat);
            }

            if (lastHr == AudioHResults.S_OK)
            {
                Format = desc;
                return client;
            }

            Marshal.ReleaseComObject(client);
            _log.Warn($"Process loopback Initialize ({desc}, flags 0x{(uint)flags:X8}) failed: {AudioHResults.Describe(lastHr)}");
            if (lastHr == AudioHResults.E_ACCESSDENIED || lastHr == AudioHResults.AUDCLNT_E_SERVICE_NOT_RUNNING) break;
        }

        throw new AudioCaptureException($"Could not initialise application capture: {AudioHResults.Describe(lastHr)}.", lastHr);
    }

    private void CaptureLoop(IAudioCaptureClient captureClient, AutoResetEvent audioEvent)
    {
        var format = Format!.Value;
        var handles = new WaitHandle[] { _stopEvent, audioEvent };
        byte[] raw = new byte[format.BlockAlign * SampleRate / 10];
        float[] stereo = new float[SampleRate / 10 * 2];

        while (true)
        {
            int signalled = WaitHandle.WaitAny(handles, 200);
            if (signalled == 0) return; // stop requested

            while (true)
            {
                int hr = captureClient.GetNextPacketSize(out uint packetFrames);
                Check(hr, "GetNextPacketSize");
                if (packetFrames == 0) break;

                hr = captureClient.GetBuffer(out IntPtr data, out uint frames, out var flags, out _, out _);
                Check(hr, "GetBuffer");
                try
                {
                    int n = (int)frames;
                    if (n == 0) continue;
                    int bytes = n * format.BlockAlign;
                    if (raw.Length < bytes) raw = new byte[bytes];
                    if (stereo.Length < n * 2) stereo = new float[n * 2];

                    if ((flags & AudioClientBufferFlags.Silent) != 0 || data == IntPtr.Zero)
                    {
                        Array.Clear(stereo, 0, n * 2);
                    }
                    else
                    {
                        Marshal.Copy(data, raw, 0, bytes);
                        SampleConverter.ToStereoFloat(raw.AsSpan(0, bytes), format, n, stereo);
                    }

                    _onData(stereo.AsSpan(0, n * 2), n, format.SampleRate);
                }
                finally
                {
                    captureClient.ReleaseBuffer(frames);
                }
            }
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr != AudioHResults.S_OK)
        {
            throw new AudioCaptureException($"Application capture failed in {what}: {AudioHResults.Describe(hr)}.", hr);
        }
    }

    public void Stop()
    {
        _stopEvent.Set();
        var t = _thread;
        if (t is not null && t != Thread.CurrentThread) t.Join(2000);
        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _stopEvent.Dispose();
    }
}
