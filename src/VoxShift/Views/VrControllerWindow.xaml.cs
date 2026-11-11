using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using VoxShift.Core.Lifecycle;
using VoxShift.Services;
using VoxShift.ViewModels;

namespace VoxShift.Views;

/// <summary>
/// VR Controller Mode: one simplified window with very large controls, meant to be used through a VR desktop
/// overlay (SteamVR desktop view, Virtual Desktop, OVR Toolkit, XSOverlay …) that forwards controller pointing as
/// normal Windows mouse input. It binds to the same <see cref="MainViewModel"/> as the full interface, so it never
/// creates its own audio engine or soundboard player; switching UI modes never touches audio.
/// </summary>
public partial class VrControllerWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LifecycleCoordinator _lifecycle;

    public VrControllerWindow(MainViewModel vm, LifecycleCoordinator? lifecycle = null)
    {
        _vm = vm;
        _lifecycle = lifecycle ?? new LifecycleCoordinator();
        DataContext = vm;
        InitializeComponent();
        RestoreGeometry();
        SourceInitialized += (_, _) => WindowStyling.ApplyDarkChrome(new WindowInteropHelper(this).Handle, 0x0F, 0x11, 0x15);
        SizeChanged += (_, _) => UpdateColumns();
        Closing += OnClosing;
    }

    /// <summary>Lets the window really close (documentation rendering only); otherwise closing returns to the full interface.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Sound tile columns follow the window width (~240 DIP per tile).</summary>
    private void UpdateColumns() => _vm.Soundboard.GridColumns = Math.Clamp((int)((ActualWidth - 40) / 240), 2, 6);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_lifecycle.IsShuttingDown) return; // closed by Application.Shutdown; geometry was saved by the shell
        SaveGeometry();
        if (AllowClose) return;
        // The window's X never quits VoxShift (no one should be locked out with a headset on): it returns to the
        // full interface. Exit stays available in the full window and the tray menu. Switching hides this window,
        // so it is queued until this handler returned (WPF forbids Hide/Show/Close while a window is closing).
        e.Cancel = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_lifecycle.IsShuttingDown) _vm.VrControllerMode = false;
        });
    }

    private void OnPageUp(object sender, RoutedEventArgs e) => SoundsScroller.PageUp();

    private void OnPageDown(object sender, RoutedEventArgs e) => SoundsScroller.PageDown();

    public void SaveGeometry()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Left) || bounds.Width <= 0) return;
        var s = _vm.Settings;
        s.VrWindowLeft = bounds.Left;
        s.VrWindowTop = bounds.Top;
        s.VrWindowWidth = bounds.Width;
        s.VrWindowHeight = bounds.Height;
        _vm.ScheduleSave();
    }

    private void RestoreGeometry()
    {
        var s = _vm.Settings;
        if (s.VrWindowWidth is { } w) Width = w;
        if (s.VrWindowHeight is { } h) Height = h;
        if (s.VrWindowLeft is not { } left || s.VrWindowTop is not { } top) return;

        // Only restore when the saved position is still on a connected screen.
        var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!area.Contains(new Point(left + 60, top + 20))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }
}
