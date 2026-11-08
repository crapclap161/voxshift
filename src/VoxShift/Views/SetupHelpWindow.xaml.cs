using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using VoxShift.Services;

namespace VoxShift.Views;

public partial class SetupHelpWindow : Window
{
    public SetupHelpWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyling.ApplyDarkChrome(new WindowInteropHelper(this).Handle, 0x14, 0x16, 0x1B);
    }

    private void OnGetCable(object sender, RoutedEventArgs e) => Open("https://vb-audio.com/Cable/");

    private void OnSoundSettings(object sender, RoutedEventArgs e) => Open("ms-settings:sound");

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {target}: {ex.Message}", "VoxShift", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
