using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VoxShift.Audio.Engine;
using VoxShift.Core.Config;
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
    private string? _smokeReport;
    private bool _cleanedUp;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

            _engine = new AudioEngine(_log, settings.Mode);
            _engine.Start();

            _vm = new MainViewModel(_engine, store, settings, _log, Dispatcher, new DialogService());
            _window = new MainWindow(_vm);
            _window.ExitRequested += OnExitRequested;
            MainWindow = _window;
            _instance?.ListenForActivation(() => Dispatcher.BeginInvoke(() => _window?.ShowFromTray()));

            bool hidden = (startMinimized || settings.StartMinimized) && settings.MinimizeToTray && _smokeReport is null;
            if (!hidden) _window.Show();

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
            _window = new MainWindow(_vm);
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
        WriteSmokeReport(sb.ToString());

        _window.ExitApplication();
        Shutdown(ok ? 0 : 1);
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

            var result = await _engine!.Soundboard.PlayAsync(sound.Id, wav);
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

    private void OnExitRequested()
    {
        Cleanup();
        Dispatcher.BeginInvoke(() => Shutdown(0), DispatcherPriority.Background);
    }

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
        Cleanup();
        _log?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        if (_smokeReport is not null)
        {
            WriteSmokeReport("FAIL\n" + e.Exception);
            Cleanup();
            Shutdown(1);
            return;
        }

        MessageBox.Show("An unexpected error occurred:\n\n" + e.Exception.Message + "\n\nThe app keeps running. Details are in the log folder.",
            "VoxShift", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
