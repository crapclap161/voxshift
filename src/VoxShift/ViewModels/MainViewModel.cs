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
    private bool _talkOverMusic;
    private bool _autoMuteMicrophone;
    private bool _vrControllerMode;
    private bool _soundboardDockVisible;
    private bool _previewMode;
    private bool _isMicrophoneLive;
    private bool _isMicrophoneMutedBySoundboard;
    private bool _isApplicationLive;
    private bool _isSoundboardPlaying;
    private string _microphoneStateText = "OFF";
    private string _transmitDetail = string.Empty;

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
        _talkOverMusic = settings.TalkOverMusicEnabled;
        _autoMuteMicrophone = settings.AutoMuteMicrophoneDuringSoundboard;
        _vrControllerMode = settings.VrControllerModeEnabled;
        _soundboardDockVisible = settings.SoundboardDockVisible;
        _engine.SetTalkOverMusic(_talkOverMusic);
        _engine.SetAutoMuteMicrophone(_autoMuteMicrophone);

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
        ToggleTalkOverMusicCommand = new RelayCommand(() => TalkOverMusic = !TalkOverMusic);
        ToggleAutoMuteMicrophoneCommand = new RelayCommand(() => AutoMuteMicrophone = !AutoMuteMicrophone);
        EnterVrControllerModeCommand = new RelayCommand(() => VrControllerMode = true);
        ExitVrControllerModeCommand = new RelayCommand(() => VrControllerMode = false);
        ToggleSoundboardDockCommand = new RelayCommand(() => SoundboardDockVisible = !SoundboardDockVisible);

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _store.Save(_settings);
        };

        Soundboard = new SoundboardViewModel(settings, engine.Soundboard, dialogs, ScheduleSave, log, engine.SetMonitorEnabled, engine.SetMonitorVolume);
        RefreshEffectiveState();

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

    public ICommand ToggleTalkOverMusicCommand { get; }

    public ICommand ToggleAutoMuteMicrophoneCommand { get; }

    public ICommand EnterVrControllerModeCommand { get; }

    public ICommand ExitVrControllerModeCommand { get; }

    public ICommand ToggleSoundboardDockCommand { get; }

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

    // ---- smart mixing ---------------------------------------------------------------------------

    /// <summary>
    /// Talk over music: in application mode VRChat hears the application <b>and</b> your microphone.
    /// Off (default) = the classic switch, where application mode never sends the microphone.
    /// </summary>
    public bool TalkOverMusic
    {
        get => _talkOverMusic;
        set
        {
            if (!Set(ref _talkOverMusic, value)) return;
            _engine.SetTalkOverMusic(value);
            _settings.TalkOverMusicEnabled = value;
            OnModeOrStatusChanged();
            ModeChanged?.Invoke();
            ScheduleSave();
        }
    }

    /// <summary>Automatically mute the microphone contribution while soundboard sounds play (default on).</summary>
    public bool AutoMuteMicrophone
    {
        get => _autoMuteMicrophone;
        set
        {
            if (!Set(ref _autoMuteMicrophone, value)) return;
            _engine.SetAutoMuteMicrophone(value);
            _settings.AutoMuteMicrophoneDuringSoundboard = value;
            OnModeOrStatusChanged();
            ModeChanged?.Invoke();
            ScheduleSave();
        }
    }

    // ---- interface ------------------------------------------------------------------------------

    /// <summary>
    /// VR Controller Mode: the full window and the dock are replaced by one simplified window with big buttons.
    /// Only the UI changes; the audio engine and the soundboard keep running untouched.
    /// </summary>
    public bool VrControllerMode
    {
        get => _vrControllerMode;
        set
        {
            if (!Set(ref _vrControllerMode, value)) return;
            _settings.VrControllerModeEnabled = value;
            ModeChanged?.Invoke();
            ScheduleSave();
        }
    }

    /// <summary>Show the soundboard dock next to the main window.</summary>
    public bool SoundboardDockVisible
    {
        get => _soundboardDockVisible;
        set
        {
            if (!Set(ref _soundboardDockVisible, value)) return;
            _settings.SoundboardDockVisible = value;
            ScheduleSave();
        }
    }

    /// <summary>Width of the expanded soundboard dock (device-independent pixels).</summary>
    public double SoundboardDockWidth
    {
        get => _settings.SoundboardDockWidth;
        set
        {
            double v = Math.Clamp(value, AppSettings.MinSoundboardDockWidth, AppSettings.MaxSoundboardDockWidth);
            if (Math.Abs(v - _settings.SoundboardDockWidth) < 0.5) return;
            _settings.SoundboardDockWidth = v;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    // ---- effective audio state (what VRChat actually hears right now) ---------------------------

    /// <summary>Your voice is reaching VRChat right now.</summary>
    public bool IsMicrophoneLive
    {
        get => _isMicrophoneLive;
        private set => Set(ref _isMicrophoneLive, value);
    }

    /// <summary>The microphone would be sent, but auto-mute holds it back while a sound plays.</summary>
    public bool IsMicrophoneMutedBySoundboard
    {
        get => _isMicrophoneMutedBySoundboard;
        private set => Set(ref _isMicrophoneMutedBySoundboard, value);
    }

    /// <summary>Application audio is reaching VRChat right now.</summary>
    public bool IsApplicationLive
    {
        get => _isApplicationLive;
        private set => Set(ref _isApplicationLive, value);
    }

    /// <summary>At least one soundboard sound is playing.</summary>
    public bool IsSoundboardPlaying
    {
        get => _isSoundboardPlaying;
        private set => Set(ref _isSoundboardPlaying, value);
    }

    /// <summary>The virtual microphone (VB-CABLE) output is running, i.e. VRChat can hear VoxShift.</summary>
    public bool IsOutputConnected => _status.OutputState == ComponentState.Running;

    /// <summary>Short microphone state for status chips: "LIVE", "MUTED" (sound playing), "OFF", "NO DEVICE", "NOT SENT".</summary>
    public string MicrophoneStateText
    {
        get => _microphoneStateText;
        private set => Set(ref _microphoneStateText, value);
    }

    public string OutputStateText => IsOutputConnected ? "ONLINE" : _status.OutputState == ComponentState.Waiting ? "WAITING" : "OFFLINE";

    public string SoundboardStateText
    {
        get
        {
            int n = Soundboard.PlayingCount;
            return n == 0 ? "IDLE" : n == 1 ? "1 PLAYING" : $"{n} PLAYING";
        }
    }

    public string ModeStateText => _mode == AudioSource.Microphone
        ? "MICROPHONE"
        : (_talkOverMusic ? "APP + MIC" : "APPLICATION");

    /// <summary>
    /// Recomputes the effective state from the engine (live) or from <see cref="MixPolicy"/> (screenshot preview).
    /// Called on every meter tick, so the UI follows the real mixer, including auto-mute starting and ending.
    /// </summary>
    public void RefreshEffectiveState()
    {
        bool output = IsOutputConnected;
        bool micRunning = _status.MicrophoneState == ComponentState.Running;
        bool appRunning = _status.ApplicationState == ComponentState.Running;
        bool soundboard = Soundboard.AnyPlaying;
        bool micOpen, appOpen, suppressed;
        if (_previewMode)
        {
            var d = MixPolicy.Decide(new MixInputs(_mode, _talkOverMusic, _autoMuteMicrophone, soundboard));
            micOpen = d.Microphone;
            appOpen = d.Application;
            suppressed = d.MicrophoneSuppressed;
        }
        else
        {
            var router = _engine.Router;
            soundboard |= router.IsSoundboardActive;
            if (output)
            {
                micOpen = router.IsMicrophoneOpen && !router.IsMicrophoneSuppressed;
                appOpen = router.IsApplicationOpen;
                suppressed = router.IsMicrophoneSuppressed;
            }
            else
            {
                // Nothing is rendered without the virtual cable; show what would happen once it is back.
                var d = MixPolicy.Decide(new MixInputs(_mode, _talkOverMusic, _autoMuteMicrophone, soundboard));
                micOpen = false;
                appOpen = false;
                suppressed = d.MicrophoneSuppressed;
            }
        }

        IsMicrophoneLive = output && micRunning && micOpen;
        IsApplicationLive = output && appRunning && appOpen;
        IsMicrophoneMutedBySoundboard = suppressed;
        IsSoundboardPlaying = soundboard;

        bool micWanted = _mode == AudioSource.Microphone || _talkOverMusic;
        MicrophoneStateText =
            suppressed ? "MUTED"
            : IsMicrophoneLive ? "LIVE"
            : !micWanted ? "OFF"
            : !micRunning ? "NO DEVICE"
            : !output ? "NOT SENT"
            : "OFF";
        TransmitDetail = BuildTransmitDetail();
        OnPropertyChanged(nameof(SoundboardStateText));
    }

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
        if (_previewMode) return;
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
        RefreshEffectiveState();
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

    public string TransmitTitle
    {
        get
        {
            if (!_status.IsTransmitting) return "NOT TRANSMITTING";
            if (_mode == AudioSource.Microphone) return "ON AIR · Microphone";
            string app = _status.ApplicationName ?? "Application";
            return _talkOverMusic ? $"ON AIR · {app} + Mic" : "ON AIR · " + app;
        }
    }

    /// <summary>One sentence describing exactly what VRChat hears right now (follows auto-mute live).</summary>
    public string TransmitDetail
    {
        get => _transmitDetail;
        private set => Set(ref _transmitDetail, value);
    }

    private string BuildTransmitDetail()
    {
        if (!_status.IsTransmitting)
        {
            if (_status.OutputState != ComponentState.Running) return _status.OutputMessage;
            return _mode == AudioSource.Microphone ? _status.MicrophoneMessage : _status.ApplicationMessage;
        }

        string app = _status.ApplicationName ?? "the application";
        string sounds = _isSoundboardPlaying ? " and your soundboard" : string.Empty;
        if (_mode == AudioSource.Microphone)
        {
            return _isMicrophoneMutedBySoundboard
                ? "VRChat hears your soundboard. Your microphone is muted until the sound ends."
                : $"VRChat hears your microphone{sounds}. Application audio is not sent.";
        }

        if (!_talkOverMusic) return $"VRChat hears {app}{sounds}. Your microphone is not sent.";
        return _isMicrophoneMutedBySoundboard
            ? $"VRChat hears {app} and your soundboard. Your microphone is muted until the sound ends."
            : $"VRChat hears {app} and your microphone{sounds}.";
    }

    public bool IsMicrophoneOnAir => _status.IsTransmitting && (_mode == AudioSource.Microphone || _talkOverMusic);

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
        _previewMode = true;
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
        RefreshEffectiveState();
    }

    private void ApplyStatus(EngineStatus status)
    {
        Status = status;
        Soundboard.OutputAvailable = status.OutputState == ComponentState.Running;
        Soundboard.MonitorAvailable = status.MonitorState == ComponentState.Running;
        Soundboard.MonitorStatus = status.MonitorMessage;
        Soundboard.MonitorStatusIsWarning = status.MonitorState is ComponentState.Error or ComponentState.Waiting;
        OnModeOrStatusChanged();
    }

    private void OnModeOrStatusChanged()
    {
        RefreshEffectiveState();
        OnPropertyChanged(nameof(IsTransmitting));
        OnPropertyChanged(nameof(TransmitTitle));
        OnPropertyChanged(nameof(IsOutputConnected));
        OnPropertyChanged(nameof(OutputStateText));
        OnPropertyChanged(nameof(ModeStateText));
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
        _engine.SetTalkOverMusic(_talkOverMusic);
        _engine.SetAutoMuteMicrophone(_autoMuteMicrophone);
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
