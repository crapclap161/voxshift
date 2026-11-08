using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoxShift.Audio.Engine;
using VoxShift.Core.Audio;
using VoxShift.ViewModels;
using VoxShift.Views;

namespace VoxShift.Services;

/// <summary>
/// Renders the real UI to PNG files for the README (<c>--render-screenshots &lt;dir&gt;</c>, run by CI).
/// Uses sample devices/applications because build machines have no microphone or virtual cable;
/// the audio engine is not started in this mode.
/// </summary>
internal static class ScreenshotRenderer
{
    private const double Scale = 2.0; // render at 192 DPI for crisp images

    public static async Task RenderAsync(MainWindow window, MainViewModel vm, string directory)
    {
        Directory.CreateDirectory(directory);
        window.SizeToContent = SizeToContent.Height;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 40;
        window.Top = 20;
        window.Show();
        await Settle();

        var mics = new[]
        {
            new DeviceItem(string.Empty, "Windows default microphone", "Microphone (Shure MV7)"),
            new DeviceItem("mic-1", "Microphone (Shure MV7)"),
            new DeviceItem("mic-2", "Headset Microphone (Valve Index)"),
        };
        var outputs = new[]
        {
            new DeviceItem(string.Empty, "Auto-detect (CABLE Input (VB-Audio Virtual Cable))"),
            new DeviceItem("out-1", "CABLE Input (VB-Audio Virtual Cable)", "virtual cable"),
            new DeviceItem("out-2", "Headphones (Valve Index)", "not a virtual cable"),
        };
        var apps = new[]
        {
            new AppItem(string.Empty, "None", null, "Do not capture any application", false, true),
            new AppItem("spotify", "Spotify", 14872, "Playing audio · 6 processes", true, true),
            new AppItem("chrome", "Google Chrome", 9324, "Playing audio · lofi hip hop radio – YouTube · 14 processes", true, true),
            new AppItem("firefox", "Firefox", 11208, "Has audio · 9 processes", false, true),
            new AppItem("vlc", "VLC media player", 7716, "No audio yet", false, true),
            new AppItem("discord", "Discord", 5480, "Has audio · 7 processes", false, true),
        };

        var status = new EngineStatus
        {
            OutputState = ComponentState.Running,
            OutputMessage = "Sending to \"CABLE Input (VB-Audio Virtual Cable)\" (48 kHz).",
            OutputDeviceName = "CABLE Input (VB-Audio Virtual Cable)",
            VrchatInputHint = "CABLE Output (VB-Audio Virtual Cable)",
            VirtualCableInstalled = true,
            MicrophoneState = ComponentState.Running,
            MicrophoneMessage = "Capturing \"Microphone (Shure MV7)\" (48 kHz).",
            MicrophoneDeviceName = "Microphone (Shure MV7)",
            ApplicationState = ComponentState.Running,
            ApplicationMessage = "Capturing \"Spotify\" (PID 14872, including its child processes).",
            ApplicationName = "Spotify",
            ApplicationProcessId = 14872,
        };

        vm.MicrophoneVolume = 100;
        vm.ApplicationVolume = 60;
        vm.Mode = AudioSource.Microphone;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.78, 0.66, 0.77);
        await Settle(400);

        // 1) Application picker (needs the real window for its popup)
        var combo = FindAll<ComboBox>(window).FirstOrDefault(c => ReferenceEquals(c.ItemsSource, vm.Applications));
        if (combo is not null)
        {
            combo.IsDropDownOpen = true;
            await Settle(600);
            if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement popupChild })
            {
                Save(popupChild, Path.Combine(directory, "application-picker.png"), useDescendantBounds: true);
            }

            combo.IsDropDownOpen = false;
            await Settle();
        }

        // 2) Main window in both modes. Rendered detached from the window so the full height fits
        //    (a real window is limited to the build machine's small screen).
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        foreach (var sv in FindAll<ScrollViewer>(root)) sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var host = new Border
        {
            Child = root,
            Width = 470,
            Background = window.Background,
            DataContext = vm,
        };
        host.Resources.MergedDictionaries.Add(window.Resources);
        System.Windows.Documents.TextElement.SetForeground(host, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);

        // Microphone mode
        vm.MicrophoneVolume = 100;
        vm.ApplicationVolume = 60;
        vm.Mode = AudioSource.Microphone;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.78, 0.66, 0.77);
        Layout(host);
        await Settle(600);
        Layout(host);
        Save(host, Path.Combine(directory, "main-microphone.png"));

        // Application mode
        vm.Mode = AudioSource.Application;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.31, 0.83, 0.82);
        Layout(host);
        await Settle(600);
        Layout(host);
        Save(host, Path.Combine(directory, "main-application.png"));

        // 3) Setup help
        var help = new SetupHelpWindow { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = 600, Top = 20 };
        help.Show();
        await Settle(600);
        var helpRoot = (FrameworkElement)help.Content;
        help.Content = null;
        foreach (var sv in FindAll<ScrollViewer>(helpRoot)) sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var helpHost = new Border { Child = helpRoot, Width = 520, Background = help.Background };
        helpHost.Resources.MergedDictionaries.Add(help.Resources);
        System.Windows.Documents.TextElement.SetForeground(helpHost, help.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(helpHost, help.FontFamily);
        Layout(helpHost);
        await Settle(300);
        Layout(helpHost);
        Save(helpHost, Path.Combine(directory, "setup-help.png"));
        help.Close();
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static void Save(FrameworkElement element, string path, bool useDescendantBounds = false)
    {
        Rect bounds = useDescendantBounds
            ? VisualTreeHelper.GetDescendantBounds(element)
            : new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        int w = (int)Math.Ceiling(bounds.Width * Scale);
        int h = (int)Math.Ceiling(bounds.Height * Scale);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, bounds.Width, bounds.Height));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task Settle(int ms = 300)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static System.Collections.Generic.IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindAll<T>(child)) yield return d;
        }
    }
}
