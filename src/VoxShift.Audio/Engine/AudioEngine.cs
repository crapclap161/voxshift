using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using VoxShift.Audio.Apps;
using VoxShift.Audio.Capture;
using VoxShift.Audio.Devices;
using VoxShift.Audio.Output;
using VoxShift.Audio.Soundboard;
using VoxShift.Core.Audio;
using VoxShift.Core.Devices;
using VoxShift.Core.Logging;

namespace VoxShift.Audio.Engine;

/// <summary>
/// Owns the complete audio graph:
/// <code>
///   Microphone (WASAPI capture) ───► mic gate ─┐
///   Application (process loopback) ► app gate ─┼─► AudioRouter (mix, limiter, meter) ─► virtual cable ─► VRChat
///   Soundboard (decoded clips) ────────────────┘
///   Soundboard (decoded clips) ─► MonitorMix ─► your headphones (soundboard only, never the microphone)
/// </code>
/// Which gates are open is decided by <see cref="MixPolicy"/> (mode, Talk over music, soundboard auto-mute).
/// All device work happens on a single dedicated MTA "engine thread" (commands are queued to it), which
/// avoids COM apartment issues and races between UI commands, device notifications and recovery.
/// A supervisor tick on that thread restarts anything that stopped (device unplugged, app closed …).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly ILog _log;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    // ---- engine-thread state ---------------------------------------------------------------------
    private DeviceService? _devices;
    private StereoRenderOutput? _output;
    private StereoRenderOutput? _monitor;
    private MMDevice? _monitorDevice;
    private bool _monitorEnabled = true;
    private DateTime _monitorRetryAt = DateTime.MinValue;
    private MMDevice? _outputDevice;
    private MicrophoneCapture? _mic;
    private MMDevice? _micDevice;
    private ProcessLoopbackCapture? _app;
    private Process? _appProcess;
    private DateTime _outputRetryAt = DateTime.MinValue;
    private DateTime _micRetryAt = DateTime.MinValue;
    private DateTime _appRetryAt = DateTime.MinValue;
    private DateTime _nextTick = DateTime.MinValue;
    private bool _devicesDirty;

    private string _desiredMicId = string.Empty;
    private string _desiredMicName = string.Empty;
    private string _desiredOutputId = string.Empty;
    private string _desiredAppExe = string.Empty;
    private string _desiredAppName = string.Empty;
    private int? _desiredAppPid;

    private EngineStatus _status = new();
    private volatile bool _disposed;

    public AudioEngine(ILog log, AudioSource initialMode = AudioSource.Microphone)
    {
        _log = log;
        Router = new AudioRouter(48000, initialMode);
        Monitor = new MonitorMix();
        Soundboard = new SoundboardPlayer(Router, Monitor, () => _output?.IsRunning == true, () => _monitor?.IsRunning == true, log);
        _status = _status with { Mode = initialMode, ProcessLoopbackSupported = ProcessLoopbackCapture.IsSupported };
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Audio engine" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>The router; exposed for meters. Mode and gains are lock-free and may be set from any thread.</summary>
    public AudioRouter Router { get; }

    /// <summary>Soundboard monitor mix played on your own default playback device.</summary>
    public MonitorMix Monitor { get; }

    /// <summary>Soundboard playback into the virtual microphone and your headphones (thread-safe).</summary>
    public SoundboardPlayer Soundboard { get; }

    /// <summary>Sets the soundboard volume (0–200 %), independent of microphone and application volume.</summary>
    public void SetSoundboardVolume(int percent) => Router.Soundboard.Volume = VolumeMath.PercentToGain(percent);

    /// <summary>Volume of the soundboard in your own headphones (0–200 %), independent of what VRChat hears.</summary>
    public void SetMonitorVolume(int percent) => Monitor.Mixer.Volume = VolumeMath.PercentToGain(percent);

    /// <summary>Turns local soundboard monitoring (hearing the sounds yourself) on or off.</summary>
    public void SetMonitorEnabled(bool enabled) => Post(() =>
    {
        if (_monitorEnabled == enabled) return;
        _monitorEnabled = enabled;
        if (!enabled) StopMonitor();
        _monitorRetryAt = DateTime.MinValue;
        Ensure();
    });

    /// <summary>Raised on the engine thread whenever <see cref="Status"/> changes.</summary>
    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Raised on the engine thread when the set of audio devices changed (refresh device lists).</summary>
    public event Action? DevicesChanged;

    public EngineStatus Status => Volatile.Read(ref _status);

    public AudioSource Mode
    {
        get => Router.RequestedSource;
        set
        {
            Router.RequestedSource = value;
            _log.Info($"Mode → {value}");
            Post(PublishStatus);
        }
    }

    /// <summary>Talk over music: in application mode, also transmit the microphone. Takes effect within one fade.</summary>
    public void SetTalkOverMusic(bool enabled)
    {
        if (Router.TalkOverMusic == enabled) return;
        Router.TalkOverMusic = enabled;
        _log.Info($"Talk over music → {(enabled ? "on" : "off")}");
    }

    /// <summary>Mute the microphone contribution (VoxShift's mix only) while soundboard sounds play.</summary>
    public void SetAutoMuteMicrophone(bool enabled)
    {
        if (Router.AutoMuteMicrophone == enabled) return;
        Router.AutoMuteMicrophone = enabled;
        _log.Info($"Auto-mute microphone during soundboard → {(enabled ? "on" : "off")}");
    }

    public void SetMicrophoneVolume(int percent) => Router.Microphone.TargetGain = VolumeMath.PercentToGain(percent);

    public void SetApplicationVolume(int percent) => Router.Application.TargetGain = VolumeMath.PercentToGain(percent);

    public void Start()
    {
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
    }

    // ---- commands (any thread) -------------------------------------------------------------------

    /// <param name="deviceId">Endpoint id, or empty for the Windows default microphone.</param>
    public void SelectMicrophone(string deviceId, string deviceName) => Post(() =>
    {
        if (_desiredMicId == deviceId && _mic is not null) return;
        _desiredMicId = deviceId ?? string.Empty;
        _desiredMicName = deviceName ?? string.Empty;
        StopMicrophone();
        _micRetryAt = DateTime.MinValue;
        Ensure();
    });

    /// <param name="deviceId">Endpoint id of the virtual cable playback side, or empty to auto-detect.</param>
    public void SelectOutput(string deviceId) => Post(() =>
    {
        if (_desiredOutputId == deviceId && _output is not null) return;
        _desiredOutputId = deviceId ?? string.Empty;
        StopOutput();
        _outputRetryAt = DateTime.MinValue;
        Ensure();
    });

    /// <param name="exeName">Executable name without ".exe"; empty clears the selection.</param>
    /// <param name="processId">Optional root process id from the list (exact instance).</param>
    public void SelectApplication(string exeName, string displayName, int? processId) => Post(() =>
    {
        string normalized = Core.Apps.ProcessTree.NormalizeExeName(exeName ?? string.Empty);
        if (normalized == _desiredAppExe && processId == _desiredAppPid && _app is not null) return;
        _desiredAppExe = normalized;
        _desiredAppName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName;
        _desiredAppPid = processId;
        StopApplication();
        _appRetryAt = DateTime.MinValue;
        Ensure();
    });

    public Task<(IReadOnlyList<AudioDeviceInfo> Capture, IReadOnlyList<AudioDeviceInfo> Render)> GetDevicesAsync() =>
        Invoke(() => (_devices!.GetDevices(DataFlow.Capture), _devices!.GetDevices(DataFlow.Render)));

    public Task<IReadOnlyList<AudioApplicationInfo>> GetApplicationsAsync() =>
        Invoke(() => AudioApplicationEnumerator.Enumerate(_devices!.Enumerator, _log));

    /// <summary>Forces an immediate re-check (e.g. user pressed "Retry").</summary>
    public void Retry() => Post(() =>
    {
        _outputRetryAt = _micRetryAt = _appRetryAt = DateTime.MinValue;
        Ensure();
    });

    // ---- engine thread ---------------------------------------------------------------------------

    private void Post(Action action)
    {
        if (_disposed) return;
        try { _queue.Add(action); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (_disposed) tcs.TrySetCanceled();
        return tcs.Task;
    }

    private void ThreadMain()
    {
        try
        {
            _devices = new DeviceService(_log);
            _devices.Changed += () => Post(() =>
            {
                // A device appeared/disappeared: retry immediately instead of waiting for back-off.
                _devicesDirty = true;
                _outputRetryAt = DateTime.MinValue;
                _micRetryAt = DateTime.MinValue;
                _monitorRetryAt = DateTime.MinValue;
                _nextTick = DateTime.UtcNow + TimeSpan.FromMilliseconds(300); // debounce bursts of notifications
            });
        }
        catch (Exception ex)
        {
            _log.Error("Could not initialise Windows audio device enumeration.", ex);
            _status = _status with
            {
                OutputState = ComponentState.Error,
                OutputMessage = "Windows audio is not available: " + ex.Message,
                MicrophoneState = ComponentState.Error,
                MicrophoneMessage = "Windows audio is not available.",
            };
        }
        finally
        {
            _ready.Set();
        }

        while (!_queue.IsCompleted)
        {
            TimeSpan wait = _nextTick - DateTime.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            try
            {
                if (_queue.TryTake(out var action, wait))
                {
                    Run(action);
                }
            }
            catch (InvalidOperationException)
            {
                break; // CompleteAdding called
            }

            if (DateTime.UtcNow >= _nextTick)
            {
                _nextTick = DateTime.UtcNow + TickInterval;
                Run(Ensure);
            }
        }

        // Shutdown on the engine thread so COM objects are released in their own apartment.
        StopMonitor();
        StopApplication();
        StopMicrophone();
        StopOutput();
        _devices?.Dispose();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { _log.Error("Engine command failed.", ex); }
    }

    /// <summary>Supervisor: brings the running graph in line with the desired configuration.</summary>
    private void Ensure()
    {
        if (_devices is null) return;

        bool devicesChanged = _devicesDirty;
        _devicesDirty = false;

        EnsureOutput();
        EnsureMicrophone(devicesChanged);
        EnsureApplication();
        EnsureMonitor();
        UpdateWarning();
        PublishStatus();

        if (devicesChanged)
        {
            try { DevicesChanged?.Invoke(); }
            catch (Exception ex) { _log.Warn("DevicesChanged handler failed.", ex); }
        }
    }

    private void EnsureOutput()
    {
        if (_output is { IsRunning: true })
        {
            // Configured device still present? (Removal also stops playback, but be defensive.)
            return;
        }

        if (_output is not null)
        {
            StopOutput();
            _outputRetryAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
        }

        if (DateTime.UtcNow < _outputRetryAt) return;

        var renders = _devices!.GetDevices(DataFlow.Render);
        var cable = VirtualCableDetector.FindPreferred(renders);
        AudioDeviceInfo? target = null;
        if (!string.IsNullOrEmpty(_desiredOutputId))
        {
            target = renders.FirstOrDefault(d => d.Id == _desiredOutputId);
            if (target is null)
            {
                SetOutputStatus(ComponentState.Waiting, "The selected output device is not connected. Waiting for it…", null, cable is not null);
                return;
            }
        }
        else
        {
            target = cable;
            if (target is null)
            {
                SetOutputStatus(ComponentState.Error,
                    "No virtual audio cable found. Install VB-CABLE (free) from vb-audio.com, then reboot – see Setup help.",
                    null, false);
                _outputRetryAt = DateTime.UtcNow + RetryDelay;
                return;
            }
        }

        var device = _devices.TryGetActive(target.Id);
        if (device is null)
        {
            SetOutputStatus(ComponentState.Waiting, $"\"{target.Name}\" is not available. Waiting…", null, cable is not null);
            return;
        }

        var output = new StereoRenderOutput(device, Router, _log);
        try
        {
            output.Stopped += (o, ex) => Post(() =>
            {
                if (!ReferenceEquals(o, _output)) return;
                _log.Warn($"Output \"{o.DeviceName}\" stopped: {ex?.Message}");
                StopOutput();
                _outputRetryAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
                Ensure();
            });
            output.Start();
            _output = output;
            _outputDevice = device;
            SetOutputStatus(ComponentState.Running, $"Sending to \"{target.Name}\" ({output.SampleRate / 1000.0:0.#} kHz).", target, cable is not null);
        }
        catch (Exception ex)
        {
            output.Dispose();
            device.Dispose();
            _outputRetryAt = DateTime.UtcNow + RetryDelay;
            _log.Error("Starting output failed.", ex);
            SetOutputStatus(ComponentState.Error, ex.Message, target, cable is not null);
        }
    }

    private void SetOutputStatus(ComponentState state, string message, AudioDeviceInfo? device, bool cableInstalled)
    {
        _status = _status with
        {
            OutputState = state,
            OutputMessage = message,
            OutputDeviceName = device?.Name,
            OutputDeviceId = device?.Id,
            VirtualCableInstalled = cableInstalled,
            VrchatInputHint = device is null ? null : VirtualCableDetector.GetVrchatInputHint(device.Name),
        };
    }

    private void EnsureMicrophone(bool devicesChanged)
    {
        if (_mic is { IsRunning: true } && devicesChanged && string.IsNullOrEmpty(_desiredMicId)
            && _devices!.GetDefaultId(DataFlow.Capture) is { } defaultId && defaultId != _mic.DeviceId)
        {
            _log.Info("Windows default microphone changed; following it.");
            StopMicrophone();
        }

        if (_mic is { IsRunning: true }) return;
        if (_mic is not null)
        {
            StopMicrophone();
            _micRetryAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
        }

        if (DateTime.UtcNow < _micRetryAt) return;

        MMDevice? device = null;
        if (!string.IsNullOrEmpty(_desiredMicId)) device = _devices!.TryGetActive(_desiredMicId);

        if (device is null && !string.IsNullOrEmpty(_desiredMicId) && !string.IsNullOrEmpty(_desiredMicName))
        {
            // Same microphone, new endpoint id (e.g. plugged into another USB port).
            var match = _devices!.GetDevices(DataFlow.Capture).FirstOrDefault(d => d.Name == _desiredMicName);
            if (match is not null) device = _devices.TryGetActive(match.Id);
        }

        if (device is null && !string.IsNullOrEmpty(_desiredMicId))
        {
            SetMicStatus(ComponentState.Waiting, $"\"{Fallback(_desiredMicName, "Selected microphone")}\" is disconnected. Waiting for it…", _desiredMicName);
            return;
        }

        device ??= _devices!.TryGetDefault(DataFlow.Capture);
        if (device is null)
        {
            SetMicStatus(ComponentState.Error, "No microphone found. Connect a microphone or enable it in Windows sound settings.", null);
            return;
        }

        string name = device.FriendlyName;
        if (VirtualCableDetector.IsVirtualCableCapture(name))
        {
            device.Dispose();
            SetMicStatus(ComponentState.Error, $"\"{name}\" is the virtual cable itself – choose your real microphone (using the cable here would create a feedback loop).", name);
            return;
        }

        var mic = new MicrophoneCapture(device, (data, frames, rate) => Router.Microphone.Write(data, frames, rate), _log);
        try
        {
            mic.Stopped += (m, ex) => Post(() =>
            {
                if (!ReferenceEquals(m, _mic)) return;
                StopMicrophone();
                _micRetryAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
                SetMicStatus(ComponentState.Waiting, "Microphone stopped (" + (ex?.Message ?? "unknown reason") + "). Reconnecting…", name);
                Ensure();
            });
            Router.Microphone.Clear();
            mic.Start();
            _mic = mic;
            _micDevice = device;
            SetMicStatus(ComponentState.Running, $"Capturing \"{name}\" ({mic.Format?.SampleRate / 1000.0:0.#} kHz).", name);
        }
        catch (Exception ex)
        {
            mic.Dispose();
            device.Dispose();
            _micRetryAt = DateTime.UtcNow + RetryDelay;
            _log.Error("Starting microphone failed.", ex);
            SetMicStatus(ComponentState.Error, ex.Message, name);
        }
    }

    private void SetMicStatus(ComponentState state, string message, string? name) =>
        _status = _status with { MicrophoneState = state, MicrophoneMessage = message, MicrophoneDeviceName = name };

    private void EnsureApplication()
    {
        if (string.IsNullOrEmpty(_desiredAppExe))
        {
            if (_app is not null) StopApplication();
            SetAppStatus(ComponentState.Idle, "Select an application to capture.", null, null);
            return;
        }

        if (!ProcessLoopbackCapture.IsSupported)
        {
            SetAppStatus(ComponentState.Error,
                $"Application capture requires Windows 11 (or Windows 10 build {ProcessLoopbackCapture.MinimumWindowsBuild}+). This PC runs build {Environment.OSVersion.Version.Build}.",
                _desiredAppName, null);
            return;
        }

        if (_app is not null)
        {
            bool exited;
            try { exited = _appProcess is null || _appProcess.HasExited; }
            catch (Exception) { exited = true; }

            if (_app.IsRunning && !exited) return;

            _log.Info(exited ? $"\"{_desiredAppName}\" exited; waiting for it to restart." : "Application capture stopped; restarting.");
            StopApplication();
            _desiredAppPid = null; // the old instance is gone; re-resolve by name
        }

        if (DateTime.UtcNow < _appRetryAt) return;

        int? pid = null;
        if (_desiredAppPid is { } hinted && IsSameApp(hinted)) pid = hinted;
        pid ??= AudioApplicationEnumerator.ResolveRoot(_desiredAppExe, _devices!.Enumerator, _log);

        if (pid is null)
        {
            SetAppStatus(ComponentState.Waiting, $"\"{_desiredAppName}\" is not running. Start it and it will be captured automatically.", _desiredAppName, null);
            _appRetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            return;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid.Value);
        }
        catch (ArgumentException)
        {
            _appRetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            return;
        }

        var capture = new ProcessLoopbackCapture(pid.Value, includeProcessTree: true,
            (data, frames, rate) => Router.Application.Write(data, frames, rate), _log);
        try
        {
            capture.Stopped += (c, ex) => Post(() =>
            {
                if (!ReferenceEquals(c, _app)) return;
                StopApplication();
                _appRetryAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
                SetAppStatus(ComponentState.Waiting, "Application capture stopped (" + (ex?.Message ?? "unknown reason") + "). Reconnecting…", _desiredAppName, null);
                Ensure();
            });
            Router.Application.Clear();
            capture.Start();
            _app = capture;
            _appProcess = process;
            _desiredAppPid = pid;
            SetAppStatus(ComponentState.Running, $"Capturing \"{_desiredAppName}\" (PID {pid}, including its child processes).", _desiredAppName, pid);
        }
        catch (Exception ex)
        {
            capture.Dispose();
            process.Dispose();
            _appRetryAt = DateTime.UtcNow + RetryDelay;
            _log.Error("Starting application capture failed.", ex);
            SetAppStatus(ComponentState.Error, ex.Message, _desiredAppName, pid);
        }
    }

    private bool IsSameApp(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && Core.Apps.ProcessTree.NormalizeExeName(p.ProcessName) == _desiredAppExe;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SetAppStatus(ComponentState state, string message, string? name, int? pid) =>
        _status = _status with { ApplicationState = state, ApplicationMessage = message, ApplicationName = name, ApplicationProcessId = pid };

    private void UpdateWarning()
    {
        string? warning = null;
        string? defaultRender = _devices!.GetDefaultId(DataFlow.Render);
        if (_output is not null && defaultRender is not null && defaultRender == _output.DeviceId)
        {
            warning = "Your Windows default playback device is the virtual cable. All system audio would go straight to VRChat " +
                      "and you would not hear it. Set your headphones/speakers as the default playback device.";
        }
        else if (_desiredAppExe == "vrchat")
        {
            warning = "You selected VRChat itself as the application – other players would hear themselves (echo). Pick a music app instead.";
        }

        _status = _status with { Warning = warning };
    }

    private void PublishStatus()
    {
        var status = _status with { Mode = Router.RequestedSource, ProcessLoopbackSupported = ProcessLoopbackCapture.IsSupported };
        _status = status;
        if (status == _lastPublished) return;
        _lastPublished = status;
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { _log.Warn("StatusChanged handler failed.", ex); }
    }

    private EngineStatus? _lastPublished;

    /// <summary>
    /// Keeps the soundboard monitor on the Windows default playback device (your headphones), following default
    /// device changes. Never monitors into the virtual cable, because VRChat would hear every sound twice.
    /// </summary>
    private void EnsureMonitor()
    {
        if (!_monitorEnabled)
        {
            if (_monitor is not null) StopMonitor();
            SetMonitorStatus(ComponentState.Idle, "Off: soundboard sounds are only sent to VRChat.", null);
            return;
        }

        string? defaultId = _devices!.GetDefaultId(DataFlow.Render);
        if (_monitor is { IsRunning: true } && _monitor.DeviceId == defaultId) return;

        if (_monitor is not null)
        {
            StopMonitor();
            _monitorRetryAt = DateTime.MinValue;
        }

        if (DateTime.UtcNow < _monitorRetryAt) return;

        var device = _devices.TryGetDefault(DataFlow.Render);
        if (device is null)
        {
            SetMonitorStatus(ComponentState.Waiting, "No playback device found for monitoring.", null);
            _monitorRetryAt = DateTime.UtcNow + RetryDelay;
            return;
        }

        string name = device.FriendlyName;
        if (!MonitorMix.IsSafeMonitorDevice(device.ID, name, _output?.DeviceId))
        {
            device.Dispose();
            SetMonitorStatus(ComponentState.Error,
                $"Your Windows default playback device is \"{name}\". Set your headphones as default to hear soundboard sounds.", name);
            _monitorRetryAt = DateTime.UtcNow + RetryDelay;
            return;
        }

        var monitor = new StereoRenderOutput(device, Monitor, _log);
        try
        {
            monitor.Stopped += (m, ex) => Post(() =>
            {
                if (!ReferenceEquals(m, _monitor)) return;
                _log.Warn($"Monitor output \"{m.DeviceName}\" stopped: {ex?.Message}");
                StopMonitor();
                _monitorRetryAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
                Ensure();
            });
            monitor.Start();
            _monitor = monitor;
            _monitorDevice = device;
            SetMonitorStatus(ComponentState.Running, $"You hear sounds on \"{name}\".", name);
        }
        catch (Exception ex)
        {
            monitor.Dispose();
            device.Dispose();
            _monitorRetryAt = DateTime.UtcNow + RetryDelay;
            _log.Warn("Starting soundboard monitor failed.", ex);
            SetMonitorStatus(ComponentState.Error, "Could not play sounds on your headphones: " + ex.Message, name);
        }
    }

    private void SetMonitorStatus(ComponentState state, string message, string? device) =>
        _status = _status with { MonitorState = state, MonitorMessage = message, MonitorDeviceName = device };

    private void StopMonitor()
    {
        var m = _monitor;
        _monitor = null;
        m?.Dispose();
        _monitorDevice?.Dispose();
        _monitorDevice = null;
        Monitor.Mixer.Clear();
    }

    private void StopOutput()
    {
        var o = _output;
        _output = null;
        o?.Dispose();
        Router.Soundboard.Clear();
        _outputDevice?.Dispose();
        _outputDevice = null;
    }

    private void StopMicrophone()
    {
        var m = _mic;
        _mic = null;
        m?.Dispose();
        _micDevice?.Dispose();
        _micDevice = null;
        Router.Microphone.Clear();
    }

    private void StopApplication()
    {
        var a = _app;
        _app = null;
        a?.Dispose();
        _appProcess?.Dispose();
        _appProcess = null;
        Router.Application.Clear();
    }

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(5));
        Soundboard.Dispose();
        _queue.Dispose();
        _ready.Dispose();
    }
}
