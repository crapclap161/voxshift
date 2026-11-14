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
using VoxShift.Audio.Capture;
using VoxShift.Audio.Soundboard;
using VoxShift.Core.Audio;
using VoxShift.Core.Config;
using VoxShift.Core.Lifecycle;
using VoxShift.Core.Logging;
using VoxShift.Views;

namespace VoxShift;

/// <summary>
/// The <c>--smoke-test</c> mode used by CI and the release workflow (scripts/smoke-test.ps1): it drives the real
/// windows and the real audio engine of the published executable and writes a plain-text report. Kept apart from
/// the composition root in App.xaml.cs because none of it runs during normal use.
/// </summary>
public partial class App
{
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
            router.Soundboard.Play("close-probe", new AudioClip(new float[48000 * 10 * 2], 48000));
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
            var router = new AudioRouter(rate, AudioSource.Application) { TalkOverMusic = true, AutoMuteMicrophone = true };
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
            router.Soundboard.Play("probe", new AudioClip(new float[rate / 10 * 2], rate));
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
            using var capture = new ProcessLoopbackCapture(
                Environment.ProcessId, includeProcessTree: true, (_, n, _) => Interlocked.Add(ref frames, n), NullLog.Instance);
            capture.Start();
            Thread.Sleep(500);
            bool running = capture.IsRunning;
            capture.Stop();
            return $"OK (format {capture.Format}, running={running}, frames={Interlocked.Read(ref frames)})";
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
            bool started = result.Status == PlayStatus.Started && _engine.Router.Soundboard.IsPlaying(sound.Id);
            _engine.Soundboard.StopAll();
            _engine.Router.Soundboard.Clear();

            string mf;
            try
            {
                var clip = await Task.Run(() => AudioFileDecoder.DecodeWithMediaFoundation(wav));
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
}
