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
            MonitorState = ComponentState.Running,
            MonitorMessage = "You hear sounds on \"Headphones (Valve Index)\".",
            MonitorDeviceName = "Headphones (Valve Index)",
        };

        // Demo soundboard (screenshot mode only: it lives in a temporary settings object and is never saved
        // to a real user library).
        var boards = new[] { new SoundboardItem("demo-1", "Stream"), new SoundboardItem("demo-2", "Memes") };
        var idle = new (string Name, double Progress, bool Missing)[]
        {
            ("Airhorn", -1, false), ("Applause", -1, false), ("Bruh", -1, false), ("Laugh", -1, false),
            ("Victory", -1, false), ("Notification", -1, false), ("Drumroll", -1, false), ("Sad Trombone", -1, false),
        };
        var playing = new (string Name, double Progress, bool Missing)[]
        {
            ("Airhorn", 0.62, false), ("Applause", -1, false), ("Bruh", -1, false), ("Laugh", 0.28, false),
            ("Victory", -1, false), ("Notification", -1, false), ("Drumroll", -1, false), ("Sad Trombone", -1, false),
        };

        vm.MicrophoneVolume = 100;
        vm.ApplicationVolume = 60;
        vm.Soundboard.Volume = 80;
        vm.Soundboard.MonitorVolume = 70;
        vm.AutoMuteMicrophone = true;
        vm.TalkOverMusic = false;
        vm.Mode = AudioSource.Microphone;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.78, 0.66, 0.77);
        vm.Soundboard.ApplyPreview(boards, idle, expanded: true, dragOver: false, dropHint: string.Empty);
        vm.RefreshEffectiveState();
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

        // The three real windows of v0.0.5. Each is shown once (so templates and styles apply) and then rendered
        // detached from its window, because a build machine's screen is smaller than the full layout.
        var mixingCard = window.FindName("MixingCard") as FrameworkElement;
        var statusCard = window.FindName("StatusCard") as FrameworkElement;
        var main = Detach(window, vm, MainWidth);

        // The real dock window of the main window (its controller is detached: nothing is docked on the build machine).
        window.DockController.Dispose();
        var dockWindow = window.Dock;
        if (!dockWindow.IsVisible) dockWindow.Show();
        await Settle(400);
        var dock = Detach(dockWindow, vm, DockWidth);

        var vrWindow = new VrControllerWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 60, Top = 40 };
        vrWindow.Show();
        await Settle(400);
        vm.Soundboard.GridColumns = 3;
        var vr = Detach(vrWindow, vm, VrWidth);

        string raw = Path.Combine(directory, "raw");
        Directory.CreateDirectory(raw);

        // 2) Full main window, classic modes (full height, no scrolling).
        vm.TalkOverMusic = false;
        vm.Mode = AudioSource.Microphone;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.78, 0.66, 0.77);
        vm.Soundboard.ApplyPreview(boards, idle, expanded: true, dragOver: false, dropHint: string.Empty);
        vm.RefreshEffectiveState();
        vm.RefreshEffectiveState();
        await RenderFullHeight(main, Path.Combine(directory, "main-microphone.png"));

        vm.Mode = AudioSource.Application;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.31, 0.83, 0.82);
        await RenderFullHeight(main, Path.Combine(directory, "main-application.png"));

        // 3) v0.0.5 main UI: application mode with Talk over music, voice and Spotify together.
        vm.TalkOverMusic = true;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.72, 0.80, 0.84);
        await RenderFullHeight(main, Path.Combine(raw, "v005-main-ui.png"));

        // 4) Audio mixing controls while a sound plays: Spotify continues, the microphone is auto-muted.
        vm.Soundboard.ApplyPreview(boards, playing, expanded: true, dragOver: false, dropHint: string.Empty);
        vm.RefreshEffectiveState();
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.70, 0.78, 0.88);
        if (mixingCard is not null && statusCard is not null)
        {
            SetScrolling(main, enabled: false);
            Layout(main, double.PositiveInfinity);
            await Settle(600);
            Layout(main, double.PositiveInfinity);
            SaveRegion(main, [statusCard, mixingCard], Path.Combine(raw, "v005-audio-mixing.png"), padding: 12);
        }

        // 5) Main window + docked soundboard, expanded, two sounds playing (microphone mode, mic auto-muted).
        vm.TalkOverMusic = false;
        vm.Mode = AudioSource.Microphone;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.74, 0.20, 0.86);
        vm.Soundboard.ApplyPreview(boards, playing, expanded: true, dragOver: false, dropHint: string.Empty);
        vm.RefreshEffectiveState();
        await RenderDocked(main, dock, DockWidth, Path.Combine(raw, "v005-soundboard-docked-expanded.png"));

        // 6) Collapsed dock: the slim strip with its expand handle.
        vm.Soundboard.ApplyPreview(boards, idle, expanded: false, dragOver: false, dropHint: string.Empty);
        vm.RefreshEffectiveState();
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.74, 0.20, 0.71);
        await RenderDocked(main, dock, SoundboardDockController.CollapsedWidth, Path.Combine(raw, "v005-soundboard-docked-collapsed.png"));

        // 7) Drag & drop target state of the dock, exactly as shown while files are dragged over it.
        vm.Soundboard.ApplyPreview(boards, idle, expanded: true, dragOver: true, dropHint: "Drop to add sounds (3 items)");
        vm.RefreshEffectiveState();
        dock.Width = DockWidth;
        dock.Height = 760;
        Layout(dock, 760);
        await Settle(600);
        Layout(dock, 760);
        Save(dock, Path.Combine(raw, "soundboard-drag-drop.png"));

        // 8) VR Controller Mode: application + talk over music, a sound playing, microphone auto-muted.
        vm.TalkOverMusic = true;
        vm.Mode = AudioSource.Application;
        vm.ApplyPreview(mics, mics[1], outputs, outputs[0], apps, apps[1], status, 0.70, 0.78, 0.88);
        vm.Soundboard.ApplyPreview(boards, playing, expanded: true, dragOver: false, dropHint: string.Empty);
        vm.RefreshEffectiveState();
        vm.RefreshEffectiveState(); // status tiles must reflect the sounds that are now playing
        vr.Height = VrHeight;
        Layout(vr, VrHeight);
        await Settle(700);
        Layout(vr, VrHeight);
        Save(vr, Path.Combine(raw, "v005-vr-controller-mode.png"));

        vrWindow.AllowClose = true;
        vrWindow.Close();
        dockWindow.Hide();
        vm.Soundboard.ApplyPreview(boards, idle, expanded: true, dragOver: false, dropHint: string.Empty);
        vm.RefreshEffectiveState();

        // 9) Setup help
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

    private const double MainWidth = 540;
    private const double DockWidth = 420;
    private const double DockedHeight = 1000;
    private const double VrWidth = 820;
    private const double VrHeight = 960;

    /// <summary>Moves a shown window's content into a detached host with the window's resources and text settings.</summary>
    private static Border Detach(Window source, MainViewModel vm, double width)
    {
        var root = (FrameworkElement)source.Content;
        source.Content = null;
        var host = new Border
        {
            Child = root,
            Width = width,
            Background = source.Background,
            DataContext = vm,
        };
        host.Resources.MergedDictionaries.Add(source.Resources);
        System.Windows.Documents.TextElement.SetForeground(host, source.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(host, source.FontFamily);
        TextOptions.SetTextFormattingMode(host, TextOptions.GetTextFormattingMode(source));
        return host;
    }

    private static void SetScrolling(FrameworkElement root, bool enabled)
    {
        foreach (var sv in FindAll<ScrollViewer>(root))
        {
            sv.VerticalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            sv.ScrollToTop();
        }
    }

    private static async Task RenderFullHeight(Border main, string path)
    {
        main.Height = double.NaN;
        SetScrolling(main, enabled: false);
        Layout(main, double.PositiveInfinity);
        await Settle(600);
        Layout(main, double.PositiveInfinity);
        Save(main, path);
    }

    /// <summary>
    /// Renders the main window and the dock side by side, exactly as the dock controller arranges them: the dock flush
    /// against the main window's right edge with the same height.
    /// </summary>
    private static async Task RenderDocked(Border main, Border dock, double dockWidth, string path)
    {
        main.Height = DockedHeight;
        SetScrolling(main, enabled: true);
        dock.Width = dockWidth;
        dock.Height = DockedHeight;
        Layout(main, DockedHeight);
        Layout(dock, DockedHeight);
        await Settle(700);
        Layout(main, DockedHeight);
        Layout(dock, DockedHeight);
        SaveSideBySide(main, dock, path);
    }

    private static void Layout(FrameworkElement element, double height)
    {
        element.Measure(new Size(element.Width, height));
        var size = double.IsInfinity(height) ? element.DesiredSize : new Size(element.Width, height);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    private static void SaveSideBySide(FrameworkElement left, FrameworkElement right, string path)
    {
        double w = left.ActualWidth + right.ActualWidth;
        double h = Math.Max(left.ActualHeight, right.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(left) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null,
                new Rect(0, 0, left.ActualWidth, left.ActualHeight));
            dc.DrawRectangle(new VisualBrush(right) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null,
                new Rect(left.ActualWidth, 0, right.ActualWidth, right.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * Scale), (int)Math.Ceiling(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Layout(FrameworkElement element) => Layout(element, double.PositiveInfinity);

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

    /// <summary>
    /// Renders <paramref name="root"/> completely (the same path as the full-window screenshots) and crops it to the
    /// bounds of <paramref name="elements"/> plus <paramref name="padding"/>, including overlays that extend outside them.
    /// </summary>
    private static void SaveRegion(FrameworkElement root, FrameworkElement[] elements, string path, double padding)
    {
        int fullW = (int)Math.Ceiling(root.ActualWidth * Scale);
        int fullH = (int)Math.Ceiling(root.ActualHeight * Scale);
        var full = new RenderTargetBitmap(fullW, fullH, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        full.Render(root);

        Rect bounds = Rect.Empty;
        foreach (var element in elements)
        {
            Rect local = VisualTreeHelper.GetDescendantBounds(element);
            local.Union(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bounds.Union(element.TransformToAncestor(root).TransformBounds(local));
        }

        bounds.Inflate(padding, padding);
        bounds.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        if (bounds.IsEmpty) return;

        var crop = new Int32Rect(
            (int)Math.Floor(bounds.X * Scale),
            (int)Math.Floor(bounds.Y * Scale),
            Math.Min(fullW - (int)Math.Floor(bounds.X * Scale), (int)Math.Ceiling(bounds.Width * Scale)),
            Math.Min(fullH - (int)Math.Floor(bounds.Y * Scale), (int)Math.Ceiling(bounds.Height * Scale)));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(full, crop)));
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
