using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
///   --minimized                   start hidden in the tray
///   --smoke-test &lt;file&gt;          start, exercise UI + engine for a few seconds, write a report, exit (used by CI;
///                                 implemented in App.SmokeTest.cs)
///   --render-screenshots &lt;dir&gt;   render the documentation screenshots with sample data and exit
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
