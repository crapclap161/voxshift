using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using VoxShift.Audio.Apps;
using VoxShift.Audio.Engine;
using VoxShift.Core.Audio;
using VoxShift.Core.Config;
using VoxShift.Core.Devices;
using VoxShift.Core.Input;
using VoxShift.Core.Logging;

namespace VoxShift.ViewModels;

/// <summary>
/// UI state and commands. Talks to <see cref="AudioEngine"/> only through its thread-safe command API
/// and status events; everything here runs on the WPF dispatcher thread.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string NoAppExe = "";

    private readonly AudioEngine _engine;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly ILog _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressSelection;
    private bool _refreshingApps;
    private bool _appsLoadedOnce;

    private DeviceItem? _selectedMicrophone;
    private DeviceItem? _selectedOutput;
    private AppItem? _selectedApplication;
    private int _micVolume;
    private int _appVolume;
    private AudioSource _mode;
    private double _micLevel;
    private double _appLevel;
    private double _outputLevel;
    private bool _isLimiting;
    private EngineStatus _status;
    private bool _alwaysOnTop;
    private bool _minimizeToTray;
    private bool _hotkeyEnabled;
    private string _hotkeyText;
    private string? _hotkeyError;
    private string? _notice;
    private bool _startMinimized;

    public MainViewModel(AudioEngine engine, SettingsStore store, AppSettings settings, ILog log, Dispatcher dispatcher, IDialogService dialogs)
    {
        _engine = engine;
        _store = store;
        _settings = settings;
        _log = log;
        _dispatcher = dispatcher;
        _status = engine.Status;

        _micVolume = settings.MicrophoneVolumePercent;
        _appVolume = settings.ApplicationVolumePercent;
        _mode = settings.Mode;
        _alwaysOnTop = settings.AlwaysOnTop;
        _minimizeToTray = settings.MinimizeToTray;
        _hotkeyEnabled = settings.HotkeyEnabled;
        _hotkeyText = settings.ToggleHotkey;
        _startMinimized = settings.StartMinimized;

        SelectMicrophoneModeCommand = new RelayCommand(() => Mode = AudioSource.Microphone);
        SelectApplicationModeCommand = new RelayCommand(() => Mode = AudioSource.Application);
        ToggleModeCommand = new RelayCommand(ToggleMode);
        RefreshApplicationsCommand = new RelayCommand(async () => await RefreshApplicationsAsync(), () => !_refreshingApps);
        RetryCommand = new RelayCommand(() => _engine.Retry());
        OpenSoundSettingsCommand = new RelayCommand(() => OpenShell("ms-settings:sound"));
        OpenRecordingDevicesCommand = new RelayCommand(() => OpenShell("control.exe", "mmsys.cpl,,1"));
        OpenVbCableSiteCommand = new RelayCommand(() => OpenShell("https://vb-audio.com/Cable/"));
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            System.IO.Directory.CreateDirectory(FileLog.DefaultDirectory);
            OpenShell(FileLog.DefaultDirectory);
        });
        DismissNoticeCommand = new RelayCommand(() => Notice = null);
        OpenSetupGuideCommand = new RelayCommand(() => SetupGuideRequested?.Invoke());

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _store.Save(_settings);
        };

        Soundboard = new SoundboardViewModel(settings, engine.Soundboard, dialogs, ScheduleSave, log);

        _engine.StatusChanged += s => _dispatcher.BeginInvoke(() => ApplyStatus(s));
        _engine.DevicesChanged += () => _dispatcher.BeginInvoke(async () => await LoadDevicesAsync());
    }

    /// <summary>Soundboard section (collections, buttons, drag &amp; drop, volume).</summary>
    public SoundboardViewModel Soundboard { get; }

    // ---- collections ----------------------------------------------------------------------------

    public ObservableCollection<DeviceItem> Microphones { get; } = new();

    public ObservableCollection<DeviceItem> OutputDevices { get; } = new();

    public ObservableCollection<AppItem> Applications { get; } = new();

    // ---- commands -------------------------------------------------------------------------------

    public ICommand SelectMicrophoneModeCommand { get; }

    public ICommand SelectApplicationModeCommand { get; }

    public ICommand ToggleModeCommand { get; }

    public ICommand RefreshApplicationsCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand OpenSoundSettingsCommand { get; }

    public ICommand OpenRecordingDevicesCommand { get; }

    public ICommand OpenVbCableSiteCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    public ICommand OpenSetupGuideCommand { get; }

    /// <summary>Raised when the view should show the setup guide window.</summary>
    public event Action? SetupGuideRequested;

    /// <summary>"VoxShift 0.0.1" for the title area.</summary>
    public string VersionText { get; } = "v" + AppInfo.Version;

    /// <summary>True while the running applications are being scanned (loading state).</summary>
    public bool IsLoadingApplications
    {
        get => _refreshingApps;
        private set
        {
            if (_refreshingApps == value) return;
            _refreshingApps = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNoApplications));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Empty state: nothing selectable besides "None".</summary>
    public bool HasNoApplications => _appsLoadedOnce && !_refreshingApps && !Applications.Any(a => a.IsRunning && a.ExeName.Length > 0);

    /// <summary>Error state with a call to action: the virtual cable driver is not installed.</summary>
    public bool ShowCableSetup => !_status.VirtualCableInstalled && _status.OutputState != ComponentState.Running && _status.OutputState != ComponentState.Waiting;

    /// <summary>Raised when the hotkey configuration changes (window re-registers it).</summary>
    public event Action? HotkeyConfigurationChanged;

    /// <summary>Raised when the transmitted mode changes (tray menu/tooltip update).</summary>
    public event Action? ModeChanged;

    // ---- selection ------------------------------------------------------------------------------

    public DeviceItem? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (!Set(ref _selectedMicrophone, value) || _suppressSelection || value is null) return;
            _settings.MicrophoneDeviceId = value.Id;
            _settings.MicrophoneDeviceName = value.Id.Length == 0 ? string.Empty : value.Name;
            _engine.SelectMicrophone(value.Id, _settings.MicrophoneDeviceName);
            ScheduleSave();
        }
    }

    public DeviceItem? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!Set(ref _selectedOutput, value) || _suppressSelection || value is null) return;
            _settings.OutputDeviceId = value.Id;
            _settings.OutputDeviceName = value.Id.Length == 0 ? string.Empty : value.Name;
            _engine.SelectOutput(value.Id);
            ScheduleSave();
        }
    }

    public AppItem? SelectedApplication
    {
        get => _selectedApplication;
        set
        {
            if (!Set(ref _selectedApplication, value) || _suppressSelection || value is null) return;
            _settings.ApplicationExeName = value.ExeName;
            _settings.ApplicationDisplayName = value.ExeName.Length == 0 ? string.Empty : value.DisplayName;
            _engine.SelectApplication(value.ExeName, value.DisplayName, value.ProcessId);
            ScheduleSave();
        }
    }

    // ---- volume & mode --------------------------------------------------------------------------

    public int MicrophoneVolume
    {
        get => _micVolume;
        set
        {
            value = VolumeMath.ClampPercent(value);
            if (!Set(ref _micVolume, value)) return;
            _engine.SetMicrophoneVolume(value);
            _settings.MicrophoneVolumePercent = value;
            ScheduleSave();
        }
    }

    public int ApplicationVolume
    {
        get => _appVolume;
        set
        {
            value = VolumeMath.ClampPercent(value);
            if (!Set(ref _appVolume, value)) return;
            _engine.SetApplicationVolume(value);
            _settings.ApplicationVolumePercent = value;
            ScheduleSave();
        }
    }

    public AudioSource Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            _engine.Mode = value;
            _settings.Mode = value;
            OnPropertyChanged(nameof(IsMicrophoneMode));
            OnPropertyChanged(nameof(IsApplicationMode));
            OnModeOrStatusChanged();
            ModeChanged?.Invoke();
            ScheduleSave();
        }
    }

    public bool IsMicrophoneMode
    {
        get => _mode == AudioSource.Microphone;
        set
        {
            if (value) Mode = AudioSource.Microphone;
        }
    }

    public bool IsApplicationMode
    {
        get => _mode == AudioSource.Application;
        set
        {
            if (value) Mode = AudioSource.Application;
        }
    }

    public void ToggleMode() => Mode = _mode == AudioSource.Microphone ? AudioSource.Application : AudioSource.Microphone;

    // ---- meters ---------------------------------------------------------------------------------

    public double MicrophoneLevel
    {
        get => _micLevel;
        private set => Set(ref _micLevel, value);
    }

    public double ApplicationLevel
    {
        get => _appLevel;
        private set => Set(ref _appLevel, value);
    }

    public double OutputLevel
    {
        get => _outputLevel;
        private set => Set(ref _outputLevel, value);
    }

    public bool IsLimiting
    {
        get => _isLimiting;
        private set => Set(ref _isLimiting, value);
    }

    public void SetMetersActive(bool active)
    {
        if (active) _meterTimer.Start();
        else
        {
            _meterTimer.Stop();
            MicrophoneLevel = ApplicationLevel = OutputLevel = 0;
        }
    }

    private void UpdateMeters()
    {
        var router = _engine.Router;
        MicrophoneLevel = Decay(MicrophoneLevel, VolumeMath.ToMeterPosition(router.Microphone.TakePeak()));
        ApplicationLevel = Decay(ApplicationLevel, VolumeMath.ToMeterPosition(router.Application.TakePeak()));
        OutputLevel = Decay(OutputLevel, VolumeMath.ToMeterPosition(router.TakeOutputPeak()));
        IsLimiting = router.IsLimiting;
        Soundboard.UpdatePlayback();
    }

    /// <summary>Instant attack, ~1 s fall time from full scale.</summary>
    private static double Decay(double current, double target) => Math.Max(target, current - 0.035);

    // ---- status ---------------------------------------------------------------------------------

    public EngineStatus Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsTransmitting => _status.IsTransmitting;

    public string TransmitTitle => _status.IsTransmitting
        ? (_mode == AudioSource.Microphone ? "ON AIR · Microphone" : "ON AIR · " + (_status.ApplicationName ?? "Application"))
        : "NOT TRANSMITTING";

    public string TransmitDetail
    {
        get
        {
            if (_status.IsTransmitting)
            {
                return _mode == AudioSource.Microphone
                    ? "VRChat hears your microphone only."
                    : $"VRChat hears {_status.ApplicationName ?? "the application"} only. Your microphone is not sent.";
            }

            if (_status.OutputState != ComponentState.Running) return _status.OutputMessage;
            return _mode == AudioSource.Microphone ? _status.MicrophoneMessage : _status.ApplicationMessage;
        }
    }

    public bool IsMicrophoneOnAir => _status.IsTransmitting && _mode == AudioSource.Microphone;

    public bool IsApplicationOnAir => _status.IsTransmitting && _mode == AudioSource.Application;

    public string VrchatHint => _status.VrchatInputHint is { } hint
        ? $"In VRChat select the microphone \"{hint}\"."
        : "Install a virtual audio cable (VB-CABLE) to create the VRChat microphone.";

    /// <summary>
    /// Documentation screenshots only (<c>--render-screenshots</c>): shows sample devices, status and meter
    /// levels without touching any audio device. Never used during normal operation.
    /// </summary>
    internal void ApplyPreview(
        IEnumerable<DeviceItem> microphones, DeviceItem microphone,
        IEnumerable<DeviceItem> outputs, DeviceItem output,
        IEnumerable<AppItem> applications, AppItem application,
        EngineStatus status, double micLevel, double appLevel, double outputLevel)
    {
        _meterTimer.Stop();
        _suppressSelection = true;
        try
        {
            Microphones.Clear();
            foreach (var m in microphones) Microphones.Add(m);
            OutputDevices.Clear();
            foreach (var o in outputs) OutputDevices.Add(o);
            Applications.Clear();
            foreach (var a in applications) Applications.Add(a);
            SelectedMicrophone = microphone;
            SelectedOutput = output;
            SelectedApplication = application;
        }
        finally
        {
            _suppressSelection = false;
        }

        ApplyStatus(status with { Mode = _mode });
        MicrophoneLevel = micLevel;
        ApplicationLevel = appLevel;
        OutputLevel = outputLevel;
        IsLimiting = false;
    }

    private void ApplyStatus(EngineStatus status)
    {
        Status = status;
        Soundboard.OutputAvailable = status.OutputState == ComponentState.Running;
        OnModeOrStatusChanged();
    }

    private void OnModeOrStatusChanged()
    {
        OnPropertyChanged(nameof(IsTransmitting));
        OnPropertyChanged(nameof(TransmitTitle));
        OnPropertyChanged(nameof(TransmitDetail));
        OnPropertyChanged(nameof(IsMicrophoneOnAir));
        OnPropertyChanged(nameof(IsApplicationOnAir));
        OnPropertyChanged(nameof(VrchatHint));
        OnPropertyChanged(nameof(ShowCableSetup));
    }

    /// <summary>Transient message (e.g. hotkey registration problem) shown in the banner.</summary>
    public string? Notice
    {
        get => _notice;
        set => Set(ref _notice, value);
    }

    // ---- options --------------------------------------------------------------------------------

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (!Set(ref _alwaysOnTop, value)) return;
            _settings.AlwaysOnTop = value;
            ScheduleSave();
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!Set(ref _minimizeToTray, value)) return;
            _settings.MinimizeToTray = value;
            ScheduleSave();
        }
    }

    public bool HotkeyEnabled
    {
        get => _hotkeyEnabled;
        set
        {
            if (!Set(ref _hotkeyEnabled, value)) return;
            _settings.HotkeyEnabled = value;
            ScheduleSave();
            HotkeyConfigurationChanged?.Invoke();
        }
    }

    /// <summary>Hotkey text such as "Ctrl+Alt+M". Only valid gestures are stored.</summary>
    public string HotkeyText
    {
        get => _hotkeyText;
        set
        {
            if (!HotkeyGesture.TryParse(value, out var gesture)) return;
            string canonical = gesture.ToString();
            if (!Set(ref _hotkeyText, canonical)) return;
            _settings.ToggleHotkey = canonical;
            ScheduleSave();
            HotkeyConfigurationChanged?.Invoke();
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!Set(ref _startMinimized, value)) return;
            _settings.StartMinimized = value;
            ScheduleSave();
        }
    }

    public string? HotkeyError
    {
        get => _hotkeyError;
        set => Set(ref _hotkeyError, value);
    }

    public AppSettings Settings => _settings;

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        _store.Save(_settings);
    }

    // ---- loading --------------------------------------------------------------------------------

    /// <summary>Pushes the persisted configuration into the engine and fills the dropdowns.</summary>
    public async Task InitializeAsync()
    {
        _engine.SetMicrophoneVolume(_micVolume);
        _engine.SetApplicationVolume(_appVolume);
        _engine.Mode = _mode;
        _engine.SelectOutput(_settings.OutputDeviceId);
        _engine.SelectMicrophone(_settings.MicrophoneDeviceId, _settings.MicrophoneDeviceName);
        _engine.SelectApplication(_settings.ApplicationExeName, _settings.ApplicationDisplayName, null);

        await LoadDevicesAsync();
        await RefreshApplicationsAsync();
    }

    public async Task LoadDevicesAsync()
    {
        IReadOnlyList<AudioDeviceInfo> capture, render;
        try
        {
            (capture, render) = await _engine.GetDevicesAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Loading devices failed.", ex);
            Notice = "Could not list audio devices: " + ex.Message;
            return;
        }

        _suppressSelection = true;
        try
        {
            // Microphones (virtual cable recording endpoints are excluded: selecting one would loop the output back).
            Microphones.Clear();
            var defaultMic = capture.FirstOrDefault(d => d.IsDefault);
            Microphones.Add(new DeviceItem(string.Empty, "Windows default microphone", defaultMic?.Name));
            foreach (var d in capture.Where(d => !VirtualCableDetector.IsVirtualCableCapture(d.Name)))
            {
                Microphones.Add(new DeviceItem(d.Id, d.Name));
            }

            string micId = _settings.MicrophoneDeviceId;
            var mic = Microphones.FirstOrDefault(m => m.Id == micId)
                      ?? (string.IsNullOrEmpty(_settings.MicrophoneDeviceName) ? null : Microphones.FirstOrDefault(m => m.Name == _settings.MicrophoneDeviceName));
            if (mic is null && !string.IsNullOrEmpty(micId))
            {
                mic = new DeviceItem(micId, Fallback(_settings.MicrophoneDeviceName, "Saved microphone") + " (disconnected)");
                Microphones.Add(mic);
            }

            SelectedMicrophone = mic ?? Microphones[0];

            // Outputs: cables first.
            OutputDevices.Clear();
            var cable = VirtualCableDetector.FindPreferred(render);
            OutputDevices.Add(new DeviceItem(string.Empty, cable is null ? "Auto-detect virtual cable (none found)" : $"Auto-detect ({cable.Name})"));
            foreach (var d in render.OrderByDescending(d => VirtualCableDetector.IsVirtualCableRender(d.Name)))
            {
                OutputDevices.Add(new DeviceItem(d.Id, d.Name, VirtualCableDetector.IsVirtualCableRender(d.Name) ? "virtual cable" : "not a virtual cable"));
            }

            string outId = _settings.OutputDeviceId;
            var output = OutputDevices.FirstOrDefault(o => o.Id == outId);
            if (output is null && !string.IsNullOrEmpty(outId))
            {
                output = new DeviceItem(outId, Fallback(_settings.OutputDeviceName, "Saved output") + " (disconnected)");
                OutputDevices.Add(output);
            }

            SelectedOutput = output ?? OutputDevices[0];
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    public async Task RefreshApplicationsAsync()
    {
        if (_refreshingApps) return;
        IsLoadingApplications = true;
        try
        {
            IReadOnlyList<AudioApplicationInfo> apps;
            try
            {
                apps = await _engine.GetApplicationsAsync();
            }
            catch (Exception ex)
            {
                _log.Error("Listing applications failed.", ex);
                Notice = "Could not list running applications: " + ex.Message;
                return;
            }

            _suppressSelection = true;
            try
            {
                Applications.Clear();
                Applications.Add(new AppItem(NoAppExe, "None", null, "Do not capture any application", false, true));
                foreach (var a in apps)
                {
                    string detail = a.IsPlaying ? "Playing audio" : a.HasAudioSession ? "Has audio" : "No audio yet";
                    if (!string.IsNullOrWhiteSpace(a.WindowTitle) && !string.Equals(a.WindowTitle, a.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        detail += " · " + Truncate(a.WindowTitle!, 48);
                    }

                    if (a.ProcessCount > 1) detail += $" · {a.ProcessCount} processes";
                    Applications.Add(new AppItem(a.ExeName, a.DisplayName, a.RootProcessId, detail, a.IsPlaying, true));
                }

                string exe = _settings.ApplicationExeName;
                AppItem? selected = Applications.FirstOrDefault(a => a.ExeName == exe && a.ProcessId == _selectedApplication?.ProcessId && exe.Length > 0)
                                    ?? Applications.FirstOrDefault(a => a.ExeName == exe);
                if (selected is null && exe.Length > 0)
                {
                    selected = new AppItem(exe, Fallback(_settings.ApplicationDisplayName, exe), null, "Not running – will be captured when it starts", false, false);
                    Applications.Add(selected);
                }

                SelectedApplication = selected ?? Applications[0];
            }
            finally
            {
                _suppressSelection = false;
            }
        }
        finally
        {
            _appsLoadedOnce = true;
            IsLoadingApplications = false;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private void OpenShell(string target, string? arguments = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (arguments is not null) psi.Arguments = arguments;
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open {target}.", ex);
            Notice = $"Could not open {target}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        SaveNow();
    }
}
