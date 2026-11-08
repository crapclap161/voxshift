using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VoxShift.Core.Audio;
using VoxShift.Core.Input;
using VoxShift.Core.Layout;
using VoxShift.Services;
using VoxShift.ViewModels;

namespace VoxShift.Views;

/// <summary>
/// The full interface and the application shell: it owns the tray icon, the global hotkey, the docked
/// <see cref="SoundboardDockWindow"/> and the optional <see cref="VrControllerWindow"/>. All windows share one
/// <see cref="MainViewModel"/>, so switching between them never restarts or duplicates any audio.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Tray icon, hotkey and dock are disposed in ExitApplication(), the window's end of life.")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TrayIcon _tray;
    private GlobalHotkey? _hotkey;
    private bool _exiting;
    private bool _trayHintShown;
    private VrControllerWindow? _vrWindow;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        RestorePosition();

        Dock = new SoundboardDockWindow(vm);
        DockController = new SoundboardDockController(this, Dock, vm);

        _tray = new TrayIcon();
        _tray.ShowRequested += ShowFromTray;
        _tray.FullInterfaceRequested += ShowFullInterface;
        _tray.MicrophoneRequested += () => _vm.Mode = AudioSource.Microphone;
        _tray.ApplicationRequested += () => _vm.Mode = AudioSource.Application;
        _tray.TalkOverMusicToggled += () => _vm.TalkOverMusic = !_vm.TalkOverMusic;
        _tray.AutoMuteToggled += () => _vm.AutoMuteMicrophone = !_vm.AutoMuteMicrophone;
        _tray.VrModeToggled += () => _vm.VrControllerMode = !_vm.VrControllerMode;
        _tray.ExitRequested += ExitApplication;
        _vm.ModeChanged += UpdateTray;
        _vm.SetupGuideRequested += ShowSetupGuide;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTray();

        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += (_, _) => UpdateMetersActive();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    /// <summary>Raised once the user really wants to quit (window close or tray "Exit").</summary>
    public event Action? ExitRequested;

    /// <summary>The docked soundboard window (a separate top-level window).</summary>
    internal SoundboardDockWindow Dock { get; }

    internal SoundboardDockController DockController { get; }

    /// <summary>The VR Controller window, created on first use.</summary>
    internal VrControllerWindow? VrWindow => _vrWindow;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        WindowStyling.ApplyDarkChrome(helper.Handle, 0x14, 0x16, 0x1B);

        var source = HwndSource.FromHwnd(helper.Handle)!;
        _hotkey = new GlobalHotkey(source);
        _hotkey.Pressed += () => _vm.ToggleMode();
        _vm.HotkeyConfigurationChanged += ApplyHotkey;
        ApplyHotkey();
    }

    /// <summary>
    /// Shows the interface the user chose: the full window with its dock, or the VR Controller window.
    /// The main window's handle always exists (it hosts the global hotkey), even while it is hidden.
    /// </summary>
    public void ShowStartupInterface()
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (_vm.VrControllerMode) ShowVrWindow();
        else
        {
            Show();
            DockController.UpdateVisibility();
        }
    }

    private void ApplyHotkey()
    {
        if (_hotkey is null) return;
        if (!_vm.HotkeyEnabled)
        {
            _hotkey.Unregister();
            _vm.HotkeyError = null;
            return;
        }

        _vm.HotkeyError = HotkeyGesture.TryParse(_vm.HotkeyText, out var gesture)
            ? _hotkey.Register(gesture)
            : "Invalid hotkey.";
    }

    /// <summary>Captures a key combination typed into the hotkey box.</summary>
    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab or Key.None)
        {
            if (key == Key.Tab) e.Handled = false; // keep keyboard navigation working
            return;
        }

        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Windows;

        var gesture = new HotkeyGesture(mods, key.ToString());
        if (!gesture.IsValid)
        {
            _vm.HotkeyError = "Use at least one modifier (Ctrl, Alt, Shift or Win) plus a key.";
            return;
        }

        _vm.HotkeyText = gesture.ToString();
        if (!_vm.HotkeyEnabled) _vm.HotkeyEnabled = true;
        else ApplyHotkey();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.TransmitTitle):
                UpdateTray();
                break;
            case nameof(MainViewModel.VrControllerMode):
                if (!_exiting) ApplyInterfaceMode();
                break;
        }
    }

    private void UpdateTray() =>
        _tray.Update(_vm.Mode == AudioSource.Microphone, _vm.TalkOverMusic, _vm.AutoMuteMicrophone, _vm.VrControllerMode, "VoxShift – " + _vm.TransmitTitle);

    // ---- interface modes (UI only; the audio engine is never touched here) -----------------------

    private void ApplyInterfaceMode()
    {
        UpdateTray();
        if (_vm.VrControllerMode)
        {
            SavePosition();
            ShowVrWindow();
            Hide(); // the dock follows (it hides with the main window)
        }
        else
        {
            _vrWindow?.SaveGeometry();
            ShowMainWindow();
            _vrWindow?.Hide();
        }

        UpdateMetersActive();
    }

    private void ShowVrWindow()
    {
        if (_vrWindow is null)
        {
            _vrWindow = new VrControllerWindow(_vm);
            _vrWindow.IsVisibleChanged += (_, _) => UpdateMetersActive();
            _vrWindow.StateChanged += (_, _) => UpdateMetersActive();
        }

        _vrWindow.Show();
        if (_vrWindow.WindowState == WindowState.Minimized) _vrWindow.WindowState = WindowState.Normal;
        _vrWindow.Activate();
        UpdateMetersActive();
    }

    private void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // bring to front reliably …
        Topmost = _vm.AlwaysOnTop; // … then restore the user's preference
        DockController.UpdateVisibility();
    }

    /// <summary>Tray "Show full interface": always leads back to the normal window, whatever state the UI is in.</summary>
    public void ShowFullInterface()
    {
        if (_vm.VrControllerMode) _vm.VrControllerMode = false; // ApplyInterfaceMode shows the main window
        else ShowMainWindow();
    }

    /// <summary>Meters and playback progress only update while some VoxShift window is on screen.</summary>
    private void UpdateMetersActive()
    {
        bool main = IsVisible && WindowState != WindowState.Minimized;
        bool vr = _vrWindow is { IsVisible: true } w && w.WindowState != WindowState.Minimized;
        _vm.SetMetersActive(main || vr);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _vm.MinimizeToTray)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloon("Still running", "VoxShift keeps routing audio from the tray. Right-click the icon to switch or exit.");
            }
        }

        UpdateMetersActive();
    }

    public void ShowFromTray()
    {
        if (_vm.VrControllerMode) ShowVrWindow();
        else ShowMainWindow();
    }

    private void ShowSetupGuide()
    {
        if (_vm.VrControllerMode) _vm.VrControllerMode = false;
        if (!IsVisible) ShowFromTray();
        new SetupHelpWindow { Owner = this }.ShowDialog();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        ExitApplication();
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        if (IsVisible || !_vm.VrControllerMode) SavePosition();
        if (_vrWindow is not null)
        {
            _vrWindow.SaveGeometry();
            _vrWindow.AllowClose = true;
            _vrWindow.Close();
        }

        DockController.Close();
        _hotkey?.Dispose();
        _tray.Dispose();
        ExitRequested?.Invoke();
        Close();
    }

    private void RestorePosition()
    {
        var s = _vm.Settings;
        if (s.WindowHeight is { } height) Height = height;
        if (s.WindowLeft is not { } left || s.WindowTop is not { } top) return;

        // Only restore if the saved position is still on a connected screen (monitors may have been unplugged).
        var monitors = NativeWindowMethods.GetAllWorkAreas();
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var saved = new PixelRect((int)(left * scale), (int)(top * scale), (int)(Width * scale), (int)(Height * scale));
        bool reachable = monitors.Count == 0
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
                .Contains(new Point(left + 40, top + 20))
            : DockPlacement.IsReachable(saved, monitors.ToArray());
        if (!reachable) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private void SavePosition()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height) : RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Left)) return;
        _vm.Settings.WindowLeft = bounds.Left;
        _vm.Settings.WindowTop = bounds.Top;
        if (bounds.Height > 0) _vm.Settings.WindowHeight = bounds.Height;
    }
}
