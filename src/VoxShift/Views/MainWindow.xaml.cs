using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VoxShift.Core.Audio;
using VoxShift.Core.Input;
using VoxShift.Services;
using VoxShift.ViewModels;

namespace VoxShift.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Tray icon and hotkey are disposed in ExitApplication(), the window's end of life.")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TrayIcon _tray;
    private GlobalHotkey? _hotkey;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        RestorePosition();

        _tray = new TrayIcon();
        _tray.ShowRequested += ShowFromTray;
        _tray.MicrophoneRequested += () => _vm.Mode = AudioSource.Microphone;
        _tray.ApplicationRequested += () => _vm.Mode = AudioSource.Application;
        _tray.ExitRequested += ExitApplication;
        _vm.ModeChanged += UpdateTray;
        _vm.SetupGuideRequested += ShowSetupGuide;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTray();

        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += (_, _) => _vm.SetMetersActive(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    /// <summary>Raised once the user really wants to quit (window close or tray "Exit").</summary>
    public event Action? ExitRequested;

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
        if (e.PropertyName is nameof(MainViewModel.TransmitTitle)) UpdateTray();
    }

    private void UpdateTray() =>
        _tray.Update(_vm.Mode == AudioSource.Microphone, "VoxShift – " + _vm.TransmitTitle);

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

        _vm.SetMetersActive(IsVisible && WindowState != WindowState.Minimized);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // bring to front reliably …
        Topmost = _vm.AlwaysOnTop; // … then restore the user's preference
    }

    // ---- Soundboard drag & drop (Windows Explorer) ----------------------------------------------

    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];

    private void OnSoundboardDragEnter(object sender, DragEventArgs e) => UpdateDragState(e);

    private void OnSoundboardDragOver(object sender, DragEventArgs e) => UpdateDragState(e);

    private void UpdateDragState(DragEventArgs e)
    {
        var paths = DroppedPaths(e);
        int count = SoundboardViewModel.CountImportable(paths);
        if (count > 0)
        {
            // "Link" matches what happens: the file is referenced, never copied or moved.
            e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy;
            _vm.Soundboard.DropHint = paths.Length == 1 && count == 1 ? "Drop to add 1 sound" : $"Drop to add sounds ({count} item{(count == 1 ? string.Empty : "s")})";
            _vm.Soundboard.IsDragOver = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            _vm.Soundboard.DropHint = "Unsupported file type";
            _vm.Soundboard.IsDragOver = paths.Length > 0;
        }

        e.Handled = true;
    }

    private void OnSoundboardDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only clear when the pointer left the card.
        var card = (FrameworkElement)sender;
        var pos = e.GetPosition(card);
        if (pos.X <= 0 || pos.Y <= 0 || pos.X >= card.ActualWidth || pos.Y >= card.ActualHeight) _vm.Soundboard.IsDragOver = false;
        e.Handled = true;
    }

    private void OnSoundboardDrop(object sender, DragEventArgs e)
    {
        _vm.Soundboard.IsDragOver = false;
        var paths = DroppedPaths(e);
        if (paths.Length > 0)
        {
            _vm.Soundboard.ImportFiles(paths);
            Activate();
        }

        e.Handled = true;
    }

    private void ShowSetupGuide()
    {
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
        SavePosition();
        _hotkey?.Dispose();
        _tray.Dispose();
        ExitRequested?.Invoke();
        Close();
    }

    private void RestorePosition()
    {
        var s = _vm.Settings;
        if (s.WindowLeft is not { } left || s.WindowTop is not { } top) return;

        // Only restore if the saved position is still on a connected screen.
        var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!area.Contains(new Point(left + 40, top + 20))) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private void SavePosition()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;
        _vm.Settings.WindowLeft = bounds.Left;
        _vm.Settings.WindowTop = bounds.Top;
    }
}
