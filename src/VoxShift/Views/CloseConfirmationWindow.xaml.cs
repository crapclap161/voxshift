using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VoxShift.Core.Lifecycle;
using VoxShift.Services;

namespace VoxShift.Views;

/// <summary>
/// "What would you like to do with VoxShift?" Shown by the shell after a close request, never from inside a
/// <c>Closing</c> handler. The result is read from <see cref="Choice"/> (Escape and the title bar X mean Cancel).
/// </summary>
public partial class CloseConfirmationWindow : Window
{
    private static int _open;
    private bool _finished;

    public CloseConfirmationWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyling.ApplyDarkChrome(new WindowInteropHelper(this).Handle, 0x0F, 0x11, 0x15);
        Loaded += (_, _) => MinimizeButton.Focus();
        Closed += (_, _) => System.Threading.Interlocked.Decrement(ref _open);
        System.Threading.Interlocked.Increment(ref _open);
    }

    /// <summary>Number of confirmation dialogs currently alive (must never exceed one).</summary>
    public static int OpenCount => System.Threading.Volatile.Read(ref _open);

    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    public bool Remember => RememberBox.IsChecked == true && Choice != CloseChoice.Cancel;

    private void OnMinimize(object sender, RoutedEventArgs e) => Finish(CloseChoice.MinimizeToTray);

    private void OnQuit(object sender, RoutedEventArgs e) => Finish(CloseChoice.Quit);

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(CloseChoice.Cancel);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Finish(CloseChoice.Cancel);
    }

    private void Finish(CloseChoice choice)
    {
        if (_finished) return;
        _finished = true;
        Choice = choice;
        Close(); // a normal click handler, not a Closing handler: closing here is safe
    }
}
