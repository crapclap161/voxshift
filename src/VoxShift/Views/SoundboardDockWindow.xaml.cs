using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using VoxShift.Core.Lifecycle;
using VoxShift.Services;
using VoxShift.ViewModels;

namespace VoxShift.Views;

/// <summary>
/// The soundboard as its own top-level window, docked to the right edge of the main window by
/// <see cref="SoundboardDockController"/>. It shares the one <see cref="MainViewModel"/> (and therefore the one
/// soundboard player) with every other window; it never owns audio state itself.
/// </summary>
public partial class SoundboardDockWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LifecycleCoordinator _lifecycle;

    public SoundboardDockWindow(MainViewModel vm, LifecycleCoordinator? lifecycle = null)
    {
        _vm = vm;
        _lifecycle = lifecycle ?? new LifecycleCoordinator();
        DataContext = vm;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowStyling.ApplyDarkChrome(hwnd, 0x0F, 0x11, 0x15);
            NativeWindowMethods.MakeToolWindow(hwnd);
        };
        Closing += OnClosing;
    }

    /// <summary>Lets the dock really close (documentation rendering only); otherwise closing only hides the dock.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when the user drags the dock header: the shell moves the main window (the dock follows).</summary>
    public event Action? MoveRequested;

    /// <summary>Raised when the dock width was changed with the edge grip.</summary>
    public event Action? WidthChangeCompleted;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // During application shutdown the dock is closed together with its owner: nothing to do, and above all no
        // visibility changes (WPF forbids Show/Hide/Close while a window is closing).
        if (AllowClose || _lifecycle.IsShuttingDown) return;

        // Alt+F4 on the dock must never quit VoxShift: just hide the soundboard (Show soundboard brings it back).
        // The hide is queued so that it runs after this handler returned and the window is no longer "closing".
        e.Cancel = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_lifecycle.IsShuttingDown) _vm.SoundboardDockVisible = false;
        });
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && e.OriginalSource is not ButtonBase) MoveRequested?.Invoke();
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e) => _vm.SoundboardDockWidth += e.HorizontalChange;

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e) => WidthChangeCompleted?.Invoke();

    /// <summary>Normal click plays/stops; in edit mode (no right-click needed) it opens the sound's actions.</summary>
    private void OnSoundTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SoundButtonViewModel sound } button) return;
        if (_vm.Soundboard.IsEditMode && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            return;
        }

        sound.PlayCommand.Execute(null);
    }

    // ---- drag & drop from Windows Explorer (expanded and collapsed) --------------------------------

    internal static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];

    private void OnDragEnter(object sender, DragEventArgs e) => UpdateDragState(e);

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDragState(e);

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

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only clear when the pointer left the window.
        var pos = e.GetPosition(Root);
        if (pos.X <= 0 || pos.Y <= 0 || pos.X >= Root.ActualWidth || pos.Y >= Root.ActualHeight) _vm.Soundboard.IsDragOver = false;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _vm.Soundboard.IsDragOver = false;
        var paths = DroppedPaths(e);
        if (paths.Length > 0) _vm.Soundboard.ImportFiles(paths); // expands the dock after a successful import
        e.Handled = true;
    }
}
