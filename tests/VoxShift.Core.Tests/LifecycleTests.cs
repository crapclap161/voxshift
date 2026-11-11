using VoxShift.Core.Config;
using VoxShift.Core.Lifecycle;

namespace VoxShift.Core.Tests;

public sealed class LifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voxshift-life-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void RapidCloseRequestsStartExactlyOneWorkflow()
    {
        var life = new LifecycleCoordinator();
        Assert.True(life.TryBeginCloseRequest());
        for (int i = 0; i < 10; i++) Assert.False(life.TryBeginCloseRequest());
        Assert.Equal(AppLifecycleState.CloseConfirmationPending, life.State);

        Assert.True(life.EndCloseRequest()); // cancelled
        Assert.Equal(AppLifecycleState.Running, life.State);
        Assert.True(life.TryBeginCloseRequest()); // a later click works again
    }

    [Fact]
    public void ShutdownRunsExactlyOnceAndBlocksEverythingElse()
    {
        var life = new LifecycleCoordinator();
        Assert.True(life.TryBeginCloseRequest());
        Assert.True(life.TryBeginShutdown());
        Assert.False(life.TryBeginShutdown());
        Assert.True(life.IsShuttingDown);
        Assert.False(life.TryBeginCloseRequest());
        Assert.False(life.TryBeginMinimizeToTray());
        Assert.False(life.EndCloseRequest());

        life.MarkExited();
        Assert.Equal(AppLifecycleState.Exited, life.State);
        Assert.True(life.IsShuttingDown);
        Assert.False(life.TryBeginShutdown());
    }

    [Fact]
    public void ShutdownIsExclusiveUnderConcurrentCallers()
    {
        var life = new LifecycleCoordinator();
        int winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (life.TryBeginShutdown()) Interlocked.Increment(ref winners);
        });
        Assert.Equal(1, winners);
    }

    [Fact]
    public void MinimizeToTrayFromPendingCloseReturnsToRunning()
    {
        var life = new LifecycleCoordinator();
        Assert.True(life.TryBeginCloseRequest());
        Assert.True(life.TryBeginMinimizeToTray());
        Assert.False(life.TryBeginMinimizeToTray());
        Assert.False(life.TryBeginCloseRequest()); // not while minimizing
        Assert.True(life.EndMinimizeToTray());
        Assert.Equal(AppLifecycleState.Running, life.State);
        Assert.False(life.IsShuttingDown);
    }

    [Fact]
    public void SavedCloseActionResolvesWithoutDialogExceptAsk()
    {
        Assert.Null(CloseButtonAction.Ask.ToChoice());
        Assert.Equal(CloseChoice.MinimizeToTray, CloseButtonAction.MinimizeToTray.ToChoice());
        Assert.Equal(CloseChoice.Quit, CloseButtonAction.Quit.ToChoice());
    }

    [Fact]
    public void CancelIsNeverRemembered()
    {
        Assert.Null(CloseButtonActionExtensions.FromRememberedChoice(CloseChoice.Cancel));
        Assert.Equal(CloseButtonAction.Quit, CloseButtonActionExtensions.FromRememberedChoice(CloseChoice.Quit));
        Assert.Equal(CloseButtonAction.MinimizeToTray, CloseButtonActionExtensions.FromRememberedChoice(CloseChoice.MinimizeToTray));
    }

    [Fact]
    public void ExistingSettingsDefaultToAskAndKeepTheirValues()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{ "SchemaVersion": 4, "MinimizeToTray": false, "MicrophoneVolumePercent": 42 }""");
        var loaded = new SettingsStore(_dir).Load();
        Assert.Equal(CloseButtonAction.Ask, loaded.CloseButtonAction);
        Assert.False(loaded.TrayNoticeShown);
        Assert.False(loaded.MinimizeToTray);
        Assert.Equal(42, loaded.MicrophoneVolumePercent);
    }

    [Theory]
    [InlineData(CloseButtonAction.Ask)]
    [InlineData(CloseButtonAction.MinimizeToTray)]
    [InlineData(CloseButtonAction.Quit)]
    public void CloseActionRoundTripsThroughSettingsFile(CloseButtonAction action)
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { CloseButtonAction = action, TrayNoticeShown = true });
        var loaded = new SettingsStore(_dir).Load();
        Assert.Equal(action, loaded.CloseButtonAction);
        Assert.True(loaded.TrayNoticeShown);
    }

    [Fact]
    public void InvalidCloseActionValueFallsBackToAsk()
    {
        var settings = new AppSettings { CloseButtonAction = (CloseButtonAction)99 }.Normalize();
        Assert.Equal(CloseButtonAction.Ask, settings.CloseButtonAction);
    }
}
