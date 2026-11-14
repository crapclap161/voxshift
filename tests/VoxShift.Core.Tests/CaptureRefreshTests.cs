using VoxShift.Core.Apps;
using VoxShift.Core.Config;

namespace VoxShift.Core.Tests;

public sealed class CaptureRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voxshift-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static ProcessTree Browser() => new(new[]
    {
        new ProcessEntry(100, 4, "explorer.exe"),
        new ProcessEntry(200, 100, "brave.exe"),   // browser process
        new ProcessEntry(201, 200, "brave.exe"),   // renderer
        new ProcessEntry(300, 200, "brave.exe"),   // audio service, started after the capture
        new ProcessEntry(500, 100, "other.exe"),
    });

    [Fact]
    public void FindsAudioProcessesThatStartedAfterTheCapture()
    {
        var known = new HashSet<int> { 200, 201 };
        var unseen = Browser().FindUnseenAudioProcesses(200, new[] { 300, 300 }, known);
        Assert.Equal(new[] { 300 }, unseen);
    }

    [Fact]
    public void IgnoresKnownAndForeignProcesses()
    {
        var known = new HashSet<int> { 200, 201, 300 };
        Assert.Empty(Browser().FindUnseenAudioProcesses(200, new[] { 300, 201 }, known));
        Assert.Empty(Browser().FindUnseenAudioProcesses(200, new[] { 500 }, new HashSet<int> { 200 }));
    }

    [Fact]
    public void SilenceAloneNeverRestartsTheCapture()
    {
        var dog = new SilentCaptureWatchdog(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(dog.ShouldRestart(now, now.AddMinutes(-5), newAudioProcessesInTree: false));
    }

    [Fact]
    public void AudibleCaptureIsNotRestartedEvenWithNewProcesses()
    {
        var dog = new SilentCaptureWatchdog(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(dog.ShouldRestart(now, now.AddMilliseconds(-300), newAudioProcessesInTree: true));
    }

    [Fact]
    public void RestartsOncePerCooldownWhenSilentWithNewAudioProcess()
    {
        var dog = new SilentCaptureWatchdog(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(dog.ShouldRestart(now, now.AddSeconds(-3), newAudioProcessesInTree: true));
        Assert.False(dog.ShouldRestart(now.AddSeconds(5), now.AddSeconds(-3), newAudioProcessesInTree: true));
        Assert.True(dog.ShouldRestart(now.AddSeconds(11), now.AddSeconds(-3), newAudioProcessesInTree: true));
    }

    [Fact]
    public void AutoRefreshIsOnByDefaultAndPersists()
    {
        Assert.True(new AppSettings().AutoRefreshApplications);

        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { AutoRefreshApplications = false });
        Assert.False(new SettingsStore(_dir).Load().AutoRefreshApplications);
    }

    [Fact]
    public void SettingsFromOlderVersionsGetAutoRefreshOn()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"ApplicationExeName\": \"brave\"}");
        var loaded = new SettingsStore(_dir).Load();
        Assert.True(loaded.AutoRefreshApplications);
        Assert.Equal("brave", loaded.ApplicationExeName);
    }
}
