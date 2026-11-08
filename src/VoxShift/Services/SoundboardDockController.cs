using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VoxShift.Core.Layout;
using VoxShift.ViewModels;
using VoxShift.Views;

namespace VoxShift.Services;

/// <summary>
/// Keeps the <see cref="SoundboardDockWindow"/> attached to the right edge of the main window:
/// <list type="bullet">
/// <item>follows moves and resizes (positions in physical pixels against the visible DWM frame, so there is no gap
/// at any DPI and on any monitor);</item>
/// <item>when a move ends or the dock expands and there is no room on that monitor, the main window is nudged left
/// so both fit; on very narrow screens the dock overlaps the main window instead of going off-screen;</item>
/// <item>hides with the main window (tray, minimize, VR Controller Mode) and comes back with it;</item>
/// <item>keeps "always on top" in sync. The dock is an owned window, so Windows keeps it above the main window and
/// closes it together with it.</item>
/// </list>
/// </summary>
internal sealed class SoundboardDockController : IDisposable
{
    /// <summary>Width of the collapsed dock strip (device-independent pixels).</summary>
    public const double CollapsedWidth = 76;

    private readonly Window _main;
    private readonly SoundboardDockWindow _dock;
    private readonly MainViewModel _vm;
    private HwndSource? _mainSource;
    private bool _positioning;
    private bool _disposed;

    public SoundboardDockController(Window main, SoundboardDockWindow dock, MainViewModel vm)
    {
        _main = main;
        _dock = dock;
        _vm = vm;

        _main.LocationChanged += OnMainGeometryChanged;
        _main.SizeChanged += OnMainGeometryChanged;
        _main.StateChanged += OnMainStateChanged;
        _main.IsVisibleChanged += OnMainVisibleChanged;
        _main.SourceInitialized += (_, _) => HookMain();
        if (new WindowInteropHelper(_main).Handle != IntPtr.Zero) HookMain();
        _dock.DpiChanged += (_, _) => Reposition(allowMoveMain: false);
        _dock.MoveRequested += () =>
        {
            var hwnd = new WindowInteropHelper(_main).Handle;
            if (hwnd != IntPtr.Zero && _main.WindowState == WindowState.Normal) NativeWindowMethods.BeginCaptionDrag(hwnd);
        };
        _dock.WidthChangeCompleted += () => Reposition(allowMoveMain: true);
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Soundboard.PropertyChanged += OnSoundboardChanged;
    }

    /// <summary>Whether the dock should be on screen right now.</summary>
    public bool IsWanted =>
        !_disposed && _vm.SoundboardDockVisible && !_vm.VrControllerMode && _main.IsVisible && _main.WindowState != WindowState.Minimized;

    public double CurrentWidthDip => _vm.Soundboard.IsExpanded ? _vm.SoundboardDockWidth : CollapsedWidth;

    private void HookMain()
    {
        if (_mainSource is not null) return;
        _mainSource = HwndSource.FromHwnd(new WindowInteropHelper(_main).Handle);
        _mainSource?.AddHook(MainWndProc);
    }

    private IntPtr MainWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // End of an interactive move/resize: make sure the dock is on screen (may nudge the main window).
        if (msg == NativeWindowMethods.WM_EXITSIZEMOVE) _main.Dispatcher.BeginInvoke(() => Reposition(allowMoveMain: true));
        return IntPtr.Zero;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SoundboardDockVisible):
            case nameof(MainViewModel.VrControllerMode):
                UpdateVisibility();
                break;
            case nameof(MainViewModel.SoundboardDockWidth):
                Reposition(allowMoveMain: false);
                break;
            case nameof(MainViewModel.AlwaysOnTop):
                _dock.Topmost = _vm.AlwaysOnTop;
                break;
        }
    }

    private void OnSoundboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundboardViewModel.IsExpanded)) Reposition(allowMoveMain: true);
    }

    private void OnMainGeometryChanged(object? sender, EventArgs e) => Reposition(allowMoveMain: false);

    private void OnMainStateChanged(object? sender, EventArgs e) => UpdateVisibility();

    private void OnMainVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateVisibility();

    /// <summary>Shows or hides the dock to match the main window and the user's choice.</summary>
    public void UpdateVisibility()
    {
        if (_disposed) return;
        if (IsWanted)
        {
            if (_dock.Owner is null && new WindowInteropHelper(_main).Handle != IntPtr.Zero) _dock.Owner = _main;
            _dock.Topmost = _vm.AlwaysOnTop;
            if (!_dock.IsVisible)
            {
                _dock.Show(); // ShowActivated=False: never steals focus from the main window or the game
            }

            Reposition(allowMoveMain: true);
        }
        else if (_dock.IsVisible)
        {
            _dock.Hide();
        }
    }

    /// <summary>Places the dock flush against the right edge of the main window's visible frame.</summary>
    public void Reposition(bool allowMoveMain)
    {
        if (_disposed || _positioning || !_dock.IsVisible) return;
        var mainHwnd = new WindowInteropHelper(_main).Handle;
        var dockHwnd = new WindowInteropHelper(_dock).Handle;
        if (mainHwnd == IntPtr.Zero || dockHwnd == IntPtr.Zero) return;

        _positioning = true;
        try
        {
            var frame = NativeWindowMethods.GetVisibleFrame(mainHwnd);
            if (frame.Width <= 0 || frame.Height <= 0) return;
            double scale = VisualTreeHelper.GetDpi(_main).DpiScaleX;
            int width = (int)Math.Round(CurrentWidthDip * scale);
            var work = NativeWindowMethods.GetWorkArea(mainHwnd);
            bool mayMove = allowMoveMain && _main.WindowState == WindowState.Normal;
            bool maximized = _main.WindowState == WindowState.Maximized; // no room to the right: overlap instead
            var layout = DockPlacement.Compute(frame, width, work.Width > 0 ? work : frame, mayMove || maximized);
            if (mayMove && layout.MainShiftX != 0) NativeWindowMethods.OffsetWindow(mainHwnd, layout.MainShiftX, 0);
            NativeWindowMethods.MoveWindow(dockHwnd, layout.Dock);
        }
        finally
        {
            _positioning = false;
        }
    }

    /// <summary>Called on exit: lets the dock really close.</summary>
    public void Close()
    {
        Dispose();
        _dock.AllowClose = true;
        _dock.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.LocationChanged -= OnMainGeometryChanged;
        _main.SizeChanged -= OnMainGeometryChanged;
        _main.StateChanged -= OnMainStateChanged;
        _main.IsVisibleChanged -= OnMainVisibleChanged;
        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.Soundboard.PropertyChanged -= OnSoundboardChanged;
        _mainSource?.RemoveHook(MainWndProc);
    }
}
