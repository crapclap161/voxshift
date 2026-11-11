using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using VoxShift.Audio.Engine;
using VoxShift.Core.Config;
using VoxShift.Core.Lifecycle;
using VoxShift.Core.Logging;
using VoxShift.Services;
using VoxShift.ViewModels;
using VoxShift.Views;

namespace VoxShift;

/// <summary>
/// Composition root. Command line:
///   --minimized            start hidden in the tray
///   --smoke-test &lt;file&gt;   start, exercise UI + engine for a few seconds, write a report, exit (used by CI)
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "WPF owns the Application lifetime; resources are released in Cleanup()/OnExit.")]
public partial class App : Application
{
    private FileLog? _log;
    private SingleInstance? _instance;
    private AudioEngine? _engine;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private readonly LifecycleCoordinator _lifecycle = new();
    private string? _smokeReport;
    private bool _cleanedUp;
    private bool _errorDialogOpen;
    private int _exitCode;
    private int _uiErrors;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The process only ends through our own Shutdown call (tray Exit, "Quit VoxShift", failed start), never as a
        // side effect of a window closing; hiding to the tray must not end it either.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SessionEnding += (_, _) => _lifecycle.TryBeginShutdown(); // Windows log-off: windows may close without asking
        var args = e.Args;
        int smokeIndex = Array.FindIndex(args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (smokeIndex >= 0) _smokeReport = smokeIndex + 1 < args.Length ? args[smokeIndex + 1] : "smoke-test.txt";
        int shotIndex = Array.FindIndex(args, a => a.Equals("--render-screenshots", StringComparison.OrdinalIgnoreCase));
        string? screenshotDir = shotIndex >= 0 ? (shotIndex + 1 < args.Length ? args[shotIndex + 1] : "screenshots") : null;
        bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        _log = new FileLog(FileLog.DefaultDirectory);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => _log?.Error("Unhandled exception.", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            _log?.Error("Unobserved task exception.", ev.Exception);
            ev.SetObserved();
        };

        if (screenshotDir is not null)
        {
            await RenderScreenshotsAsync(screenshotDir);
            return;
        }

        if (_smokeReport is null)
        {
            _instance = SingleInstance.TryAcquire();
            if (_instance is null)
            {
                // Another instance was asked to show its window.
                Shutdown(0);
                return;
            }
        }

        _log.Info($"{AppInfo.DisplayName} starting on Windows build {Environment.OSVersion.Version.Build}.");

        try
        {
            var store = new SettingsStore(SettingsStore.DefaultDirectory, _log);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VOXSHIFT_SETTINGS_DIR")))
            {
                store.TryMigrateFrom(SettingsStore.LegacyDirectory);
            }

            var settings = store.Load();
            if (_smokeReport is not null) settings.VrControllerModeEnabled = false; // the smoke test starts in the full interface

            _engine = new AudioEngine(_log, settings.Mode);
            _engine.Start();

            _vm = new MainViewModel(_engine, store, settings, _log, Dispatcher, new DialogService());
            _window = new MainWindow(_vm, _lifecycle);
            _window.ExitRequested += OnExitRequested;
            MainWindow = _window;
            _instance?.ListenForActivation(() => Dispatcher.BeginInvoke(() => _window?.ShowFromTray()));

            bool hidden = (startMinimized || settings.StartMinimized) && settings.MinimizeToTray && _smokeReport is null;
            if (hidden) new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle(); // hotkey works from the tray
            else _window.ShowStartupInterface();

            await _vm.InitializeAsync();
            if (!hidden) _vm.SetMetersActive(true);

            if (_smokeReport is not null) await RunSmokeTestAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Fatal startup error.", ex);
            if (_smokeReport is not null)
            {
                WriteSmokeReport("FAIL\n" + ex);
                Cleanup();
                Shutdown(1);
                return;
            }

            MessageBox.Show("VoxShift could not start:\n\n" + ex.Message + "\n\nDetails were written to the log in " + FileLog.DefaultDirectory,
                "VoxShift", MessageBoxButton.OK, MessageBoxImage.Error);
            Cleanup();
            Shutdown(1);
        }
    }

    /// <summary>Documentation mode: renders the UI with sample data; the audio engine is never started.</summary>
    private async Task RenderScreenshotsAsync(string directory)
    {
        int exitCode = 0;
        try
        {
            string tempSettings = Path.Combine(Path.GetTempPath(), "voxshift-screenshots-" + Guid.NewGuid().ToString("N"));
            var store = new SettingsStore(tempSettings, _log);
            var settings = new AppSettings { HotkeyEnabled = true, ToggleHotkey = "Ctrl+Alt+M", MinimizeToTray = true };
            _engine = new AudioEngine(_log!, settings.Mode);
            _vm = new MainViewModel(_engine, store, settings, _log!, Dispatcher, new DialogService());
            _window = new MainWindow(_vm, _lifecycle);
            MainWindow = _window;
            await ScreenshotRenderer.RenderAsync(_window, _vm, Path.GetFullPath(directory));
            _log!.Info("Screenshots written to " + Path.GetFullPath(directory));
        }
        catch (Exception ex)
        {
            _log?.Error("Rendering screenshots failed.", ex);
            exitCode = 1;
        }

        _window?.ExitApplication();
        Shutdown(exitCode);
    }

    /// <summary>
    /// CI check: proves that the XAML (including the Fluent theme) loads, the engine thread runs, device and
    /// application enumeration work and mode switching does not throw. Audio devices may be absent on CI.
    /// </summary>
    private async Task RunSmokeTestAsync()
    {
        var sb = new StringBuilder();
        _vm!.ToggleMode();
        await Task.Delay(500);
        _vm.ToggleMode();
        await _vm.RefreshApplicationsAsync();
        await Task.Delay(3000);

        var status = _engine!.Status;
        bool ok = _window!.IsLoaded && _window.IsVisible;
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(ok ? "OK" : "FAIL: window not visible");
        sb.AppendLine(inv, $"Version: {AppInfo.Version}");
        sb.AppendLine(inv, $"Window title: {_window.Title}");
        sb.AppendLine(inv, $"Mode: {status.Mode}");
        sb.AppendLine(inv, $"Output: {status.OutputState} – {status.OutputMessage}");
        sb.AppendLine(inv, $"Microphone: {status.MicrophoneState} – {status.MicrophoneMessage}");
        sb.AppendLine(inv, $"Application: {status.ApplicationState} – {status.ApplicationMessage}");
        sb.AppendLine(inv, $"Process loopback supported: {status.ProcessLoopbackSupported}");
        sb.AppendLine(inv, $"Microphones listed: {_vm.Microphones.Count}");
        sb.AppendLine(inv, $"Outputs listed: {_vm.OutputDevices.Count}");
        sb.AppendLine(inv, $"Applications listed: {_vm.Applications.Count}");
        foreach (var a in _vm.Applications.Take(15)) sb.AppendLine(inv, $"  - {a.DisplayName} [{a.ExeName}] pid={a.ProcessId} {a.Detail}");
        sb.AppendLine("Process loopback probe (own process): " + await Task.Run(ProbeProcessLoopback));
        sb.AppendLine("Soundboard probe: " + await ProbeSoundboardAsync());
        sb.AppendLine(inv, $"Soundboard monitor: {_engine.Status.MonitorState} – {_engine.Status.MonitorMessage}");
        string mix = ProbeSmartMixing();
        sb.AppendLine("Smart mixing probe: " + mix);
        string ui = await ProbeInterfaceModesAsync();
        sb.AppendLine("Interface probe: " + ui);
        string close = await ProbeCloseLifecycleAsync();
        sb.AppendLine("Close lifecycle probe: " + close);
        ok &= mix.StartsWith("OK", StringComparison.Ordinal) && ui.StartsWith("OK", StringComparison.Ordinal)
              && close.StartsWith("OK", StringComparison.Ordinal);
        WriteSmokeReport((ok ? string.Empty : "FAIL: smart mixing or interface probe\n") + sb.ToString());

        // Leave exactly like a user would: the X button with "Quit VoxShift" saved. The process must end by itself
        // (scripts/smoke-test.ps1 fails if it does not exit within the timeout).
        _exitCode = ok ? 0 : 1;
        _vm.CloseAction = CloseButtonAction.Quit;
        _window.Close();
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe)
        where T : class
    {
        for (int i = 0; i < 40; i++)
        {
            if (probe() is { } value) return value;
            await Task.Delay(100);
        }

        return null;
    }

    private static void Click(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static CloseConfirmationWindow? FindCloseDialog() =>
        Current.Windows.OfType<CloseConfirmationWindow>().FirstOrDefault();

    /// <summary>
    /// Drives the real window lifecycle (the same code path as the X button): exactly one confirmation dialog for
    /// repeated clicks, Cancel, Minimize to Tray with "remember", restore without duplicate windows, remembered
    /// actions, collapsed dock, a playing sound, VR Controller Mode, and that the audio engine is never replaced.
    /// The final Quit is the caller's job.
    /// </summary>
    private async Task<string> ProbeCloseLifecycleAsync()
    {
        try
        {
            var engine = _engine!;
            var router = engine.Router;
            var player = engine.Soundboard;
            var win = _window!;
            var dock = win.Dock;
            var vm = _vm!;
            var life = _lifecycle;
            int errorsBefore = _uiErrors;
            vm.VrControllerMode = false;
            vm.SoundboardDockVisible = true;
            vm.Soundboard.IsExpanded = true;
            await Task.Delay(400);
            if (!win.IsVisible) win.ShowFromTray();

            // 1) Always ask: rapid X clicks open exactly one dialog; Cancel changes nothing.
            vm.CloseAction = CloseButtonAction.Ask;
            for (int i = 0; i < 4; i++) win.Close();
            var dialog = await WaitForAsync(FindCloseDialog);
            await Task.Delay(300);
            bool oneDialog = dialog is not null && CloseConfirmationWindow.OpenCount == 1 && life.State == AppLifecycleState.CloseConfirmationPending;
            if (dialog is not null) Click(dialog.CancelButton);
            await Task.Delay(300);
            bool cancelKept = win.IsVisible && dock.IsVisible && life.State == AppLifecycleState.Running && CloseConfirmationWindow.OpenCount == 0;

            // 2) Minimize to Tray + "Remember my choice" while a sound plays and the dock is expanded.
            router.Soundboard.Play("close-probe", new VoxShift.Core.Audio.AudioClip(new float[48000 * 10 * 2], 48000));
            win.Close();
            dialog = await WaitForAsync(FindCloseDialog);
            if (dialog is not null)
            {
                dialog.RememberBox.IsChecked = true;
                Click(dialog.MinimizeButton);
            }

            await Task.Delay(400);
            bool hidden = !win.IsVisible && !dock.IsVisible && life.State == AppLifecycleState.Running;
            bool remembered = vm.CloseAction == CloseButtonAction.MinimizeToTray;
            bool audioUntouched = ReferenceEquals(engine, _engine) && ReferenceEquals(router, engine.Router) && ReferenceEquals(player, engine.Soundboard)
                                  && router.Soundboard.IsPlaying("close-probe");

            // 3) Restore from the tray: same windows, no duplicates.
            win.ShowFromTray();
            await Task.Delay(400);
            bool restored = win.IsVisible && dock.IsVisible && ReferenceEquals(win.Dock, dock)
                            && Current.Windows.OfType<SoundboardDockWindow>().Count() == 1 && Current.Windows.OfType<MainWindow>().Count() == 1;

            // 4) Remembered action: no dialog, repeated clicks run the workflow once.
            int handled = win.CloseRequestsHandled;
            for (int i = 0; i < 4; i++) win.Close();
            await Task.Delay(400);
            bool noDialog = CloseConfirmationWindow.OpenCount == 0 && !win.IsVisible && !dock.IsVisible && win.CloseRequestsHandled == handled + 1;
            win.ShowFromTray();
            await Task.Delay(300);

            // 5) Collapsed dock survives hide and restore.
            vm.Soundboard.IsExpanded = false;
            await Task.Delay(300);
            win.Close();
            await Task.Delay(400);
            bool collapsedHidden = !win.IsVisible && !dock.IsVisible;
            win.ShowFromTray();
            await Task.Delay(400);
            bool collapsedRestored = win.IsVisible && dock.IsVisible && !vm.Soundboard.IsExpanded;
            vm.Soundboard.IsExpanded = true;
            await Task.Delay(300);

            // 6) VR Controller Mode: its X returns to the full interface; tray minimize and restore work.
            vm.VrControllerMode = true;
            await Task.Delay(600);
            var vr = win.VrWindow;
            bool vrUp = vr is { IsVisible: true } && !win.IsVisible;
            vr?.Close();
            await Task.Delay(600);
            bool vrBack = !vm.VrControllerMode && win.IsVisible && vr is { IsVisible: false };
            vm.VrControllerMode = true;
            await Task.Delay(600);
            win.HideToTray();
            await Task.Delay(300);
            bool vrHidden = vr is { IsVisible: false } && !win.IsVisible && vm.VrControllerMode;
            win.ShowFromTray();
            await Task.Delay(400);
            bool vrRestored = vr is { IsVisible: true } && !win.IsVisible;
            vm.VrControllerMode = false;
            await Task.Delay(600);

            router.Soundboard.Clear();
            bool noErrors = _uiErrors == errorsBefore;
            bool ok = oneDialog && cancelKept && hidden && remembered && audioUntouched && restored && noDialog && collapsedHidden
                      && collapsedRestored && vrUp && vrBack && vrHidden && vrRestored && noErrors && win.IsVisible;
            return $"{(ok ? "OK" : "FAILED")} (one dialog={oneDialog}, cancel keeps open={cancelKept}, minimize hides all={hidden}, remembered={remembered}, " +
                   $"audio untouched={audioUntouched}, restore without duplicates={restored}, remembered action once without dialog={noDialog}, " +
                   $"collapsed hide/restore={collapsedHidden}/{collapsedRestored}, VR up/back/tray/restore={vrUp}/{vrBack}/{vrHidden}/{vrRestored}, no UI errors={noErrors})";
        }
        catch (Exception ex)
        {
            return "FAILED: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Exercises the real engine's router with the settings toggles: talk over music and auto-mute during a soundboard
    /// voice, using an isolated router so that the running graph and the user's devices are not disturbed.
    /// </summary>
    private static string ProbeSmartMixing()
    {
        try
        {
            const int rate = 48000, block = 480;
            var router = new VoxShift.Core.Audio.AudioRouter(rate, VoxShift.Core.Audio.AudioSource.Application) { TalkOverMusic = true, AutoMuteMicrophone = true };
            var mic = new float[block * 2];
            var app = new float[block * 2];
            Array.Fill(mic, 0.25f);
            Array.Fill(app, -0.5f);
            var output = new float[block * 2];
            void Step()
            {
                router.Microphone.Write(mic, block, rate);
                router.Application.Write(app, block, rate);
                router.Render(output, block);
            }

            for (int i = 0; i < 20; i++) Step();
            float both = output[^1]; // -0.25 = app + mic
            router.Soundboard.Play("probe", new VoxShift.Core.Audio.AudioClip(new float[rate / 10 * 2], rate));
            for (int i = 0; i < 5; i++) Step();
            float muted = output[^1]; // -0.5 = app only
            bool suppressed = router.IsMicrophoneSuppressed;
            for (int i = 0; i < 40; i++) Step();
            float restored = output[^1];
            bool ok = Math.Abs(both + 0.25f) < 0.01f && Math.Abs(muted + 0.5f) < 0.01f && suppressed && Math.Abs(restored + 0.25f) < 0.01f
                      && !router.IsMicrophoneSuppressed;
            var inv = CultureInfo.InvariantCulture;
            return (ok ? "OK" : "FAILED") + string.Format(inv, " (app+mic {0:0.00}, during sound {1:0.00}, after {2:0.00})", both, muted, restored);
        }
        catch (Exception ex)
        {
            return "FAILED: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Shows the soundboard dock, collapses and expands it, switches to VR Controller Mode and back, and checks that
    /// the audio engine and its soundboard player stay the very same instances (no restart, no duplicates).
    /// </summary>
    private async Task<string> ProbeInterfaceModesAsync()
    {
        try
        {
            var engine = _engine!;
            var router = engine.Router;
            var player = engine.Soundboard;
            _vm!.SoundboardDockVisible = true;
            _vm.Soundboard.IsExpanded = true;
            await Task.Delay(400);
            var dock = _window!.Dock;
            bool dockShown = dock.IsVisible && dock.IsLoaded;
            var main = new System.Windows.Rect(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
            bool dockRight = dock.Left >= main.Right - 24 && Math.Abs(dock.Top - main.Top) < 24;
            _vm.Soundboard.IsExpanded = false;
            await Task.Delay(300);
            bool collapsed = dock.IsVisible && dock.ActualWidth < 120;
            _vm.Soundboard.IsExpanded = true;
            await Task.Delay(300);

            _vm.VrControllerMode = true;
            await Task.Delay(600);
            var vr = _window.VrWindow;
            bool vrShown = vr is { IsVisible: true, IsLoaded: true } && !_window.IsVisible && !dock.IsVisible;
            _vm.VrControllerMode = false;
            await Task.Delay(600);
            bool back = _window.IsVisible && dock.IsVisible && vr is { IsVisible: false };
            bool sameEngine = ReferenceEquals(engine, _engine) && ReferenceEquals(router, _engine.Router) && ReferenceEquals(player, _engine.Soundboard);

            bool ok = dockShown && dockRight && collapsed && vrShown && back && sameEngine;
            return $"{(ok ? "OK" : "FAILED")} (dock shown={dockShown}, right of main={dockRight}, collapsed strip={collapsed}, " +
                   $"VR mode shown={vrShown}, back to full={back}, same audio engine={sameEngine})";
        }
        catch (Exception ex)
        {
            return "FAILED: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>Activates, initialises, starts and stops a process loopback capture of this process.</summary>
    private static string ProbeProcessLoopback()
    {
        try
        {
            long frames = 0;
            using var capture = new VoxShift.Audio.Capture.ProcessLoopbackCapture(
                Environment.ProcessId, includeProcessTree: true, (_, n, _) => System.Threading.Interlocked.Add(ref frames, n), NullLog.Instance);
            capture.Start();
            System.Threading.Thread.Sleep(500);
            bool running = capture.IsRunning;
            capture.Stop();
            return $"OK (format {capture.Format}, running={running}, frames={System.Threading.Interlocked.Read(ref frames)})";
        }
        catch (Exception ex)
        {
            return $"FAILED: {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})";
        }
    }

    /// <summary>
    /// Writes a real WAV file, imports it through the soundboard view model (as a drop would), decodes, resamples
    /// and starts it in the mixer, then stops it. Also reports whether Windows Media Foundation can decode.
    /// </summary>
    private async Task<string> ProbeSoundboardAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "voxshift-smoke-sounds " + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            Directory.CreateDirectory(dir);
            string wav = Path.Combine(dir, "Smoke tëst #1.wav");
            WriteTestWav(wav, 44100, 0.4);

            _vm!.Soundboard.ImportFiles([wav, Path.Combine(dir, "notes.txt")]);
            var sound = _vm.Soundboard.Sounds.FirstOrDefault(s => s.FilePath == wav);
            if (sound is null) return "FAILED: import did not add the file";

            var result = await _engine!.Soundboard.PlayAsync(sound.Id, wav, ignoreOutputState: true);
            bool started = result.Status == VoxShift.Audio.Soundboard.PlayStatus.Started && _engine.Router.Soundboard.IsPlaying(sound.Id);
            _engine.Soundboard.StopAll();
            _engine.Router.Soundboard.Clear();

            string mf;
            try
            {
                var clip = await Task.Run(() => VoxShift.Audio.Soundboard.AudioFileDecoder.DecodeWithMediaFoundation(wav));
                mf = $"Media Foundation OK ({clip.SampleRate} Hz, {clip.Frames} frames)";
            }
            catch (Exception ex)
            {
                mf = "Media Foundation unavailable: " + ex.Message;
            }

            bool fileUntouched = File.Exists(wav) && new FileInfo(wav).Length == 44 + (int)(44100 * 0.4) * 4;
            return started && fileUntouched
                ? $"OK (imported, decoded, resampled to {_engine.Router.OutputRate} Hz, played; original file untouched; {mf})"
                : $"FAILED (status {result.Status}: {result.Message}; started={started}; untouched={fileUntouched}; {mf})";
        }
        catch (Exception ex)
        {
            return "FAILED: " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void WriteTestWav(string path, int rate, double seconds)
    {
        int frames = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + frames * 4);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(rate);
        w.Write(rate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(frames * 4);
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            w.Write(v);
            w.Write(v);
        }
    }

    private void WriteSmokeReport(string text)
    {
        try
        {
            File.WriteAllText(_smokeReport!, text);
        }
        catch (Exception ex)
        {
            _log?.Error("Could not write smoke test report.", ex);
        }
    }

    /// <summary>Raised once by the main window's shutdown sequence. Windows are closed by Shutdown; cleanup runs in OnExit.</summary>
    private void OnExitRequested() => Shutdown(_exitCode);

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        try { _vm?.Dispose(); } catch (Exception ex) { _log?.Error("Saving settings failed.", ex); }
        try { _engine?.Dispose(); } catch (Exception ex) { _log?.Error("Stopping audio engine failed.", ex); }
        _instance?.Dispose();
        _instance = null;
        _log?.Info("Exited.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifecycle.TryBeginShutdown();
        Cleanup();
        _lifecycle.MarkExited();
        StartExitWatchdog(_log, _exitCode);
        _log?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Safety net, not a workaround: the shutdown sequence has finished, so if some foreign foreground thread still keeps
    /// the process alive after a generous delay, end it (and say so in the log) instead of leaving a hidden process.
    /// </summary>
    private static void StartExitWatchdog(FileLog? log, int exitCode)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(10_000);
            try { log?.Warn("The process did not exit 10 s after shutdown; forcing exit."); } catch (ObjectDisposedException) { }
            Environment.Exit(exitCode);
        })
        {
            IsBackground = true,
            Name = "VoxShift exit watchdog",
        };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        _uiErrors++;
        if (_smokeReport is not null)
        {
            WriteSmokeReport("FAIL\n" + e.Exception);
            Cleanup();
            Shutdown(1);
            return;
        }

        // Once shutdown has begun nobody can act on a dialog: log only. While one error dialog is open (it pumps
        // messages), further errors are logged but never stacked as duplicate dialogs.
        if (_lifecycle.IsShuttingDown || _errorDialogOpen) return;
        _errorDialogOpen = true;
        try
        {
            MessageBox.Show("An unexpected error occurred:\n\n" + e.Exception.Message + "\n\nThe app keeps running. Details are in the log folder.",
                "VoxShift", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _errorDialogOpen = false;
        }
    }
}
