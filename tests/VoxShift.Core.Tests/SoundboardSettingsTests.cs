using VoxShift.Core.Config;
using VoxShift.Core.Soundboard;

namespace VoxShift.Core.Tests;

public sealed class SoundboardSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voxshift-sb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void SoundboardsRoundTripThroughSettingsFile()
    {
        var store = new SettingsStore(_dir);
        var settings = new AppSettings
        {
            SoundboardVolumePercent = 135,
            SoundboardDockExpanded = false,
            Soundboards =
            [
                new SoundboardSet { Name = "Memes", Sounds = [new SoundEntry { Name = "Bruh", FilePath = @"D:\Sound FX\bruh ♪.mp3" }] },
                new SoundboardSet { Name = "Stream" },
            ],
        };
        settings.SelectedSoundboardId = settings.Soundboards[1].Id;

        store.Save(settings);
        var loaded = new SettingsStore(_dir).Load();

        Assert.Equal(135, loaded.SoundboardVolumePercent);
        Assert.False(loaded.SoundboardDockExpanded);
        Assert.False(loaded.SoundboardExpanded); // legacy mirror for a downgrade to v0.0.4
        Assert.Equal(["Memes", "Stream"], loaded.Soundboards.Select(s => s.Name));
        Assert.Equal(settings.Soundboards[1].Id, loaded.SelectedSoundboardId);
        var sound = Assert.Single(loaded.Soundboards[0].Sounds);
        Assert.Equal("Bruh", sound.Name);
        Assert.Equal(@"D:\Sound FX\bruh ♪.mp3", sound.FilePath);
        Assert.Equal(settings.Soundboards[0].Sounds[0].Id, sound.Id);
        Assert.DoesNotContain("base64", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsFromVersion002LoadWithSoundboardDefaults()
    {
        Directory.CreateDirectory(_dir);
        // A settings.json as written by VoxShift 0.0.2 (no soundboard keys).
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            {
              "SchemaVersion": 1,
              "MicrophoneDeviceId": "{0.0.1.00000000}.{abc}",
              "ApplicationExeName": "spotify",
              "Mode": "Application",
              "MicrophoneVolumePercent": 120,
              "ApplicationVolumePercent": 55,
              "HotkeyEnabled": true,
              "ToggleHotkey": "Ctrl+Alt+M"
            }
            """);

        var loaded = new SettingsStore(_dir).Load();

        Assert.Equal("spotify", loaded.ApplicationExeName);
        Assert.Equal(120, loaded.MicrophoneVolumePercent);
        Assert.True(loaded.HotkeyEnabled);
        Assert.Empty(loaded.Soundboards);
        Assert.Equal(80, loaded.SoundboardVolumePercent);
        Assert.True(loaded.SoundboardExpanded);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void BrokenSoundboardEntriesAreRepairedOnLoad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            {
              "SoundboardVolumePercent": 999,
              "SelectedSoundboardId": "does-not-exist",
              "Soundboards": [
                { "Id": "", "Name": "", "Sounds": [ { "Id": "x", "Name": "", "FilePath": "C:\\a\\horn_loud.wav" }, { "Id": "x", "FilePath": "C:\\b.wav" }, { "FilePath": "" } ] },
                null
              ]
            }
            """);

        var loaded = new SettingsStore(_dir).Load();

        var board = Assert.Single(loaded.Soundboards);
        Assert.False(string.IsNullOrEmpty(board.Id));
        Assert.Equal("Soundboard", board.Name);
        Assert.Equal(2, board.Sounds.Count);
        Assert.NotEqual(board.Sounds[0].Id, board.Sounds[1].Id);
        Assert.Equal(board.Id, loaded.SelectedSoundboardId);
        Assert.Equal(200, loaded.SoundboardVolumePercent);
    }
}
