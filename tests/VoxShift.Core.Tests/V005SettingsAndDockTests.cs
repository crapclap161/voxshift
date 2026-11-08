using VoxShift.Core.Audio;
using VoxShift.Core.Config;
using VoxShift.Core.Layout;
using VoxShift.Core.Soundboard;

namespace VoxShift.Core.Tests;

/// <summary>v0.0.5 settings (schema 4), migration from v0.0.4 and the soundboard dock geometry.</summary>
public sealed class V005SettingsAndDockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voxshift-v005-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private AppSettings LoadJson(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
        return new SettingsStore(_dir).Load();
    }

    [Fact]
    public void NewSettingsHaveSafeDefaults()
    {
        var s = new AppSettings().Normalize();
        Assert.Equal(4, AppSettings.CurrentSchemaVersion);
        Assert.True(s.AutoMuteMicrophoneDuringSoundboard);
        Assert.False(s.TalkOverMusicEnabled);
        Assert.False(s.VrControllerModeEnabled);
        Assert.True(s.SoundboardDockVisible);
        Assert.True(s.SoundboardDockExpanded);
        Assert.Equal(AppSettings.DefaultSoundboardDockWidth, s.SoundboardDockWidth);
    }

    [Fact]
    public void NewSettingsRoundTrip()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings
        {
            AutoMuteMicrophoneDuringSoundboard = false,
            TalkOverMusicEnabled = true,
            VrControllerModeEnabled = true,
            SoundboardDockVisible = false,
            SoundboardDockExpanded = false,
            SoundboardDockWidth = 512,
            VrWindowLeft = -1500,
            VrWindowTop = 30,
            VrWindowWidth = 900,
            VrWindowHeight = 1000,
            WindowHeight = 870,
        });

        var loaded = new SettingsStore(_dir).Load();
        Assert.False(loaded.AutoMuteMicrophoneDuringSoundboard);
        Assert.True(loaded.TalkOverMusicEnabled);
        Assert.True(loaded.VrControllerModeEnabled);
        Assert.False(loaded.SoundboardDockVisible);
        Assert.False(loaded.SoundboardDockExpanded);
        Assert.Equal(512, loaded.SoundboardDockWidth);
        Assert.Equal(-1500, loaded.VrWindowLeft); // monitors left of the primary one have negative coordinates
        Assert.Equal(30, loaded.VrWindowTop);
        Assert.Equal(900, loaded.VrWindowWidth);
        Assert.Equal(1000, loaded.VrWindowHeight);
        Assert.Equal(870, loaded.WindowHeight);
    }

    [Fact]
    public void V004SettingsMigrateWithoutLosingAnything()
    {
        var loaded = LoadJson("""
            {
              "SchemaVersion": 3,
              "MicrophoneDeviceId": "{0.0.1.00000000}.{mic}",
              "MicrophoneDeviceName": "Microphone (Shure MV7)",
              "OutputDeviceId": "{0.0.0.00000000}.{cable}",
              "ApplicationExeName": "spotify",
              "Mode": "Application",
              "MicrophoneVolumePercent": 120,
              "ApplicationVolumePercent": 45,
              "AlwaysOnTop": true,
              "HotkeyEnabled": true,
              "ToggleHotkey": "Ctrl+Shift+F9",
              "SoundboardVolumePercent": 90,
              "SoundboardExpanded": false,
              "SoundboardMonitorEnabled": false,
              "SoundboardMonitorVolumePercent": 33,
              "WindowLeft": 100,
              "WindowTop": 50,
              "Soundboards": [ { "Id": "b1", "Name": "Memes", "Sounds": [ { "Id": "s1", "Name": "Bruh", "FilePath": "D:\\Sounds\\bruh.mp3" } ] } ],
              "SelectedSoundboardId": "b1"
            }
            """);

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        // Everything from v0.0.4 is preserved …
        Assert.Equal("{0.0.1.00000000}.{mic}", loaded.MicrophoneDeviceId);
        Assert.Equal("Microphone (Shure MV7)", loaded.MicrophoneDeviceName);
        Assert.Equal("{0.0.0.00000000}.{cable}", loaded.OutputDeviceId);
        Assert.Equal("spotify", loaded.ApplicationExeName);
        Assert.Equal(AudioSource.Application, loaded.Mode);
        Assert.Equal(120, loaded.MicrophoneVolumePercent);
        Assert.Equal(45, loaded.ApplicationVolumePercent);
        Assert.True(loaded.AlwaysOnTop);
        Assert.True(loaded.HotkeyEnabled);
        Assert.Equal("Ctrl+Shift+F9", loaded.ToggleHotkey);
        Assert.Equal(90, loaded.SoundboardVolumePercent);
        Assert.False(loaded.SoundboardMonitorEnabled);
        Assert.Equal(33, loaded.SoundboardMonitorVolumePercent);
        Assert.Equal(100, loaded.WindowLeft);
        Assert.Equal(50, loaded.WindowTop);
        var board = Assert.Single(loaded.Soundboards);
        Assert.Equal("b1", board.Id);
        Assert.Equal("b1", loaded.SelectedSoundboardId);
        var sound = Assert.Single(board.Sounds);
        Assert.Equal(("s1", "Bruh", @"D:\Sounds\bruh.mp3"), (sound.Id, sound.Name, sound.FilePath));

        // … and the new options start safely: auto-mute on, talk over music off, normal interface,
        // the soundboard visible and expanded for someone who already uses it.
        Assert.True(loaded.AutoMuteMicrophoneDuringSoundboard);
        Assert.False(loaded.TalkOverMusicEnabled);
        Assert.False(loaded.VrControllerModeEnabled);
        Assert.True(loaded.SoundboardDockVisible);
        Assert.True(loaded.SoundboardDockExpanded);
    }

    [Fact]
    public void V004UserWithoutSoundsKeepsTheirCollapsedState()
    {
        var loaded = LoadJson("""{ "SchemaVersion": 3, "SoundboardExpanded": false }""");
        Assert.False(loaded.SoundboardDockExpanded);
        Assert.True(loaded.SoundboardDockVisible);
    }

    [Fact]
    public void MigratedSettingsAreSavedAsSchema4AndStayStable()
    {
        var loaded = LoadJson("""{ "SchemaVersion": 2, "SoundboardExpanded": true }""");
        loaded.TalkOverMusicEnabled = true;
        loaded.SoundboardDockExpanded = false;
        new SettingsStore(_dir).Save(loaded);

        var again = new SettingsStore(_dir).Load();
        Assert.Equal(4, again.SchemaVersion);
        Assert.True(again.TalkOverMusicEnabled); // not reset by a second migration
        Assert.False(again.SoundboardDockExpanded);
        Assert.False(again.SoundboardExpanded); // legacy mirror kept in sync for downgrades
    }

    [Fact]
    public void InvalidWindowValuesAreRepaired()
    {
        var loaded = LoadJson("""
            { "SchemaVersion": 4, "SoundboardDockWidth": 5, "VrWindowWidth": 99999, "VrWindowHeight": -3, "WindowHeight": 1 }
            """);
        Assert.Equal(AppSettings.MinSoundboardDockWidth, loaded.SoundboardDockWidth);
        Assert.Equal(4000, loaded.VrWindowWidth);
        Assert.Equal(360, loaded.VrWindowHeight);
        Assert.Equal(420, loaded.WindowHeight);
    }

    [Fact]
    public void CollectionsSurviveSavingWithNewOptions()
    {
        var settings = new AppSettings { VrControllerModeEnabled = true };
        var library = new SoundboardLibrary(settings);
        var board = library.CreateCollection("Stream");
        settings.Soundboards[0].Sounds.Add(new SoundEntry { Name = "Airhorn", FilePath = @"C:\Sounds\airhorn.wav" });
        new SettingsStore(_dir).Save(settings);

        var loaded = new SettingsStore(_dir).Load();
        Assert.Equal(board.Id, Assert.Single(loaded.Soundboards).Id);
        Assert.Equal(@"C:\Sounds\airhorn.wav", Assert.Single(loaded.Soundboards[0].Sounds).FilePath);
        Assert.True(loaded.VrControllerModeEnabled);
    }

    // ---- dock geometry --------------------------------------------------------------------------

    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void DockSitsFlushAgainstTheRightEdgeWithTheSameHeight()
    {
        var main = new PixelRect(200, 100, 540, 900);
        var layout = DockPlacement.Compute(main, 420, Work, allowMoveMain: true);
        Assert.Equal(new PixelRect(740, 100, 420, 900), layout.Dock);
        Assert.Equal(0, layout.MainShiftX);
        Assert.False(layout.Overlaps);
    }

    [Fact]
    public void DockFollowsTheMainWindowWhileDragging()
    {
        var main = new PixelRect(1700, 100, 540, 900); // partly off-screen during a drag
        var layout = DockPlacement.Compute(main, 420, Work, allowMoveMain: false);
        Assert.Equal(main.Right, layout.Dock.Left);
        Assert.Equal(0, layout.MainShiftX);
    }

    [Fact]
    public void MainWindowIsNudgedLeftWhenTheDockWouldLeaveTheScreen()
    {
        var main = new PixelRect(1300, 100, 540, 900);
        var layout = DockPlacement.Compute(main, 420, Work, allowMoveMain: true);
        Assert.Equal(-340, layout.MainShiftX); // 1300 + 540 + 420 = 2260 → 340 px too far right
        Assert.Equal(Work.Right, layout.Dock.Right);
        Assert.Equal(main.Right - 340, layout.Dock.Left);
        Assert.False(layout.Overlaps);
    }

    [Fact]
    public void DockOverlapsInsteadOfGoingOffScreenOnNarrowMonitors()
    {
        var narrow = new PixelRect(0, 0, 800, 600);
        var main = new PixelRect(0, 0, 540, 600);
        var layout = DockPlacement.Compute(main, 420, narrow, allowMoveMain: true);
        Assert.True(layout.Overlaps);
        Assert.Equal(narrow.Right, layout.Dock.Right);
        Assert.True(layout.Dock.Left >= narrow.Left);
    }

    [Fact]
    public void DockWorksOnASecondaryMonitorWithNegativeCoordinates()
    {
        var left = new PixelRect(-2560, 0, 2560, 1400); // monitor to the left of the primary one
        var main = new PixelRect(-1200, 50, 540, 900);
        var layout = DockPlacement.Compute(main, 420, left, allowMoveMain: true);
        Assert.Equal(-240, layout.Dock.Right); // -1200 + 540 + 420
        Assert.Equal(0, layout.MainShiftX);

        var nearEdge = new PixelRect(-600, 50, 540, 900); // dock would cross onto the primary monitor
        layout = DockPlacement.Compute(nearEdge, 420, left, allowMoveMain: true);
        Assert.Equal(0, layout.Dock.Right);
        Assert.Equal(-360, layout.MainShiftX);
    }

    [Fact]
    public void DockScalesWithDpi()
    {
        // 150 % scaling: everything is in physical pixels, so a 420 DIP dock is 630 px wide.
        var main = new PixelRect(100, 100, 810, 1350);
        var layout = DockPlacement.Compute(main, (int)Math.Round(420 * 1.5), new PixelRect(0, 0, 3840, 2100), allowMoveMain: true);
        Assert.Equal(new PixelRect(910, 100, 630, 1350), layout.Dock);
    }

    [Fact]
    public void SavedPositionsOnUnpluggedMonitorsAreRejected()
    {
        var monitors = new[] { new PixelRect(0, 0, 1920, 1040) };
        Assert.True(DockPlacement.IsReachable(new PixelRect(100, 100, 540, 900), monitors));
        Assert.False(DockPlacement.IsReachable(new PixelRect(-2000, 100, 540, 900), monitors));
        Assert.False(DockPlacement.IsReachable(new PixelRect(100, 1200, 540, 900), monitors));
        Assert.True(DockPlacement.IsReachable(new PixelRect(-2000, 100, 540, 900), [.. monitors, new PixelRect(-2560, 0, 2560, 1400)]));
    }
}
