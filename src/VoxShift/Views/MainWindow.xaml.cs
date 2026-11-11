using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VoxShift.Core.Audio;
using VoxShift.Core.Config;
using VoxShift.Core.Input;
using VoxShift.Core.Layout;
using VoxShift.Core.Lifecycle;
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
    private readonly LifecycleCoordinator _lifecycle;
    private GlobalHotkey? _hotkey;
    private VrControllerWindow? _vrWindow;

    public MainWindow(MainViewModel vm, LifecycleCoordinator? lifecycle = null)
    {
        _vm = vm;
        _lifecycle = lifecycle ?? new LifecycleCoordinator();
        DataContext = vm;
        InitializeComponent();

        RestorePosition();

        Dock = new SoundboardDockWindow(vm, _lifecycle);
        DockController = new SoundboardDockController(this, Dock, vm, _lifecycle);

        _tray = new TrayIcon();
        _tray.ShowRequested += ShowFromTray;
        _tray.FullInterfaceRequested += ShowFullInterface;
        _tray.MicrophoneRequested += () => _vm.Mode = AudioSource.Microphone;
        _tray.ApplicationRequested += () => _vm.Mode = AudioSource.Application;
        _tray.TalkOverMusicToggled += () => _vm.TalkOverMusic = !_vm.TalkOverMusic;
        _tray.AutoMuteToggled += () => _vm.AutoMuteMicrophone = !_vm.AutoMuteMicrophone;
        _tray.VrModeToggled += () => _vm.VrControllerMode = !_vm.VrControllerMode;
        // Queued so that the tray menu has finished its click handling before the tray icon is disposed.
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);
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

    /// <summary>Number of close requests (X clicks) that actually started the close workflow; repeats are ignored.</summary>
    internal int CloseRequestsHandled { get; private set; }

    /// <summary>The VR Controller window, created on first use.</summary>
    internal VrControllerWindow? VrWindow => _vrWindow;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        WindowStyling.ApplyDarkChrome(helper.Handle, 0x0F, 0x11, 0x15);

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
        if (_lifecycle.IsShuttingDown) return;
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
                if (!_lifecycle.IsShuttingDown) ApplyInterfaceMode();
                break;
        }
    }

    private void UpdateTray() =>
        _tray.Update(_vm.Mode == AudioSource.Microphone, _vm.TalkOverMusic, _vm.AutoMuteMicrophone, _vm.VrControllerMode, "VoxShift – " + _vm.TransmitTitle);

    // ---- interface modes (UI only; the audio engine is never touched here) -----------------------

    private void ApplyInterfaceMode()
    {
        if (_lifecycle.IsShuttingDown) return;
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
        if (_lifecycle.IsShuttingDown) return;
        if (_vrWindow is null)
        {
            _vrWindow = new VrControllerWindow(_vm, _lifecycle);
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
        if (_lifecycle.IsShuttingDown) return; // never restore a window that is closing
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
        if (_lifecycle.IsShuttingDown) return;
        if (WindowState == WindowState.Minimized && _vm.MinimizeToTray) HideToTray();
        UpdateMetersActive();
    }

    public void ShowFromTray()
    {
        if (_lifecycle.IsShuttingDown) return;
        if (_vm.VrControllerMode) ShowVrWindow();
        else ShowMainWindow();
    }

    private void ShowSetupGuide()
    {
        if (_lifecycle.IsShuttingDown) return;
        if (_vm.VrControllerMode) _vm.VrControllerMode = false;
        if (!IsVisible) ShowFromTray();
        new SetupHelpWindow { Owner = this }.ShowDialog();
    }

    // ---- closing: one coordinated workflow ---------------------------------------------------------

    /// <summary>
    /// The X button. The close is always cancelled here and only a request is queued: WPF forbids Show, Hide, Close
    /// and modal dialogs while a window is closing, so nothing else happens inside this handler. The main window is
    /// only ever really closed by <c>Application.Shutdown</c>, after the shutdown sequence started.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_lifecycle.IsShuttingDown) return; // the final close performed by Application.Shutdown / session end
        e.Cancel = true;
        RequestClose();
    }

    private void RequestClose()
    {
        // Repeated X clicks, Alt+F4 and a pending dialog all collapse into the single workflow that is already running.
        if (!_lifecycle.TryBeginCloseRequest()) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, HandleCloseRequest);
    }

    private void HandleCloseRequest()
    {
        if (_lifecycle.IsShuttingDown) return; // stale callback
        CloseRequestsHandled++;
        CloseChoice choice = _vm.CloseAction.ToChoice() ?? AskCloseChoice();
        if (_lifecycle.IsShuttingDown) return; // e.g. "Exit VoxShift" from the tray while the dialog was open

        switch (choice)
        {
            case CloseChoice.MinimizeToTray:
                HideToTray();
                break;
            case CloseChoice.Quit:
                ExitApplication();
                break;
            default:
                _lifecycle.EndCloseRequest(); // Cancel: nothing else changes, audio is not touched
                break;
        }
    }

    private CloseChoice AskCloseChoice()
    {
        var dialog = new CloseConfirmationWindow();
        Window? owner = IsVisible ? this : _vrWindow is { IsVisible: true } vr ? vr : null;
        if (owner is not null) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
        CloseChoice choice = dialog.Choice;
        if (dialog.Remember && CloseButtonActionExtensions.FromRememberedChoice(choice) is { } action) _vm.CloseAction = action;
        return choice;
    }

    /// <summary>
    /// Hides every VoxShift window and keeps running in the tray: microphone / application routing and the soundboard
    /// continue untouched (only windows change; the audio engine is never involved).
    /// </summary>
    internal void HideToTray()
    {
        if (!_lifecycle.TryBeginMinimizeToTray()) return;
        try
        {
            if (IsVisible || !_vm.VrControllerMode) SavePosition();
            _vrWindow?.SaveGeometry();
            if (_vrWindow is { IsVisible: true }) _vrWindow.Hide();
            if (IsVisible) Hide(); // the dock hides with it (SoundboardDockController); its state stays in the settings
            DockController.UpdateVisibility();
            UpdateMetersActive();
            ShowTrayNoticeOnce();
        }
        finally
        {
            _lifecycle.EndMinimizeToTray();
        }
    }

    private void ShowTrayNoticeOnce()
    {
        if (_vm.Settings.TrayNoticeShown) return;
        _vm.Settings.TrayNoticeShown = true;
        _vm.ScheduleSave();
        _tray.ShowBalloon("VoxShift", "VoxShift is still running in the system tray.");
    }

    /// <summary>
    /// The one and only shutdown sequence (X-button "Quit", saved "Quit" preference, tray "Exit VoxShift"). It runs
    /// exactly once, never from inside a Closing handler, and it does not close or show any window itself:
    /// <c>Application.Shutdown</c> (raised through <see cref="ExitRequested"/>) closes the remaining windows, whose
    /// Closing handlers return immediately because the lifecycle says "shutting down".
    /// </summary>
    public void ExitApplication()
    {
        if (!_lifecycle.TryBeginShutdown()) return;
        if (IsVisible || !_vm.VrControllerMode) SavePosition();
        _vrWindow?.SaveGeometry();
        DockController.Dispose(); // unsubscribes: nothing can re-show the dock any more
        _vm.ModeChanged -= UpdateTray;
        _vm.SetupGuideRequested -= ShowSetupGuide;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.HotkeyConfigurationChanged -= ApplyHotkey;
        _hotkey?.Dispose();
        _tray.Dispose();
        ExitRequested?.Invoke();
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
