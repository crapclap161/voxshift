using VoxShift.Core.Audio;
using VoxShift.Core.Config;

namespace VoxShift.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voxshift-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void RoundTripsAllValues()
    {
        var store = new SettingsStore(_dir);
        var s = new AppSettings
        {
            MicrophoneDeviceId = "{0.0.1.00000000}.{abc}",
            MicrophoneDeviceName = "Mic (USB)",
            OutputDeviceId = "{0.0.0.00000000}.{def}",
            ApplicationExeName = "spotify",
            ApplicationDisplayName = "Spotify",
            Mode = AudioSource.Application,
            MicrophoneVolumePercent = 135,
            ApplicationVolumePercent = 42,
            AlwaysOnTop = true,
            MinimizeToTray = false,
            HotkeyEnabled = true,
            ToggleHotkey = "Ctrl+Shift+F9",
            WindowLeft = 10,
            WindowTop = 20,
        };

        store.Save(s);
        var loaded = new SettingsStore(_dir).Load();

        Assert.Equal(s.MicrophoneDeviceId, loaded.MicrophoneDeviceId);
        Assert.Equal(s.OutputDeviceId, loaded.OutputDeviceId);
        Assert.Equal("spotify", loaded.ApplicationExeName);
        Assert.Equal(AudioSource.Application, loaded.Mode);
        Assert.Equal(135, loaded.MicrophoneVolumePercent);
        Assert.Equal(42, loaded.ApplicationVolumePercent);
        Assert.True(loaded.AlwaysOnTop);
        Assert.False(loaded.MinimizeToTray);
        Assert.True(loaded.HotkeyEnabled);
        Assert.Equal("Ctrl+Shift+F9", loaded.ToggleHotkey);
        Assert.Equal(10, loaded.WindowLeft);
    }

    [Fact]
    public void ImportsSettingsFromPreviousVersionOnce()
    {
        string legacy = Path.Combine(_dir, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"ApplicationExeName\": \"spotify\", \"MicrophoneVolumePercent\": 150}");
        var store = new SettingsStore(Path.Combine(_dir, "new"));

        Assert.True(store.TryMigrateFrom(legacy));
        var loaded = store.Load();
        Assert.Equal("spotify", loaded.ApplicationExeName);
        Assert.Equal(150, loaded.MicrophoneVolumePercent);

        // Existing settings are never overwritten by a later import.
        loaded.MicrophoneVolumePercent = 80;
        store.Save(loaded);
        Assert.False(store.TryMigrateFrom(legacy));
        Assert.Equal(80, store.Load().MicrophoneVolumePercent);
    }

    [Fact]
    public void MigrationWithoutLegacySettingsDoesNothing()
    {
        var store = new SettingsStore(Path.Combine(_dir, "new"));
        Assert.False(store.TryMigrateFrom(Path.Combine(_dir, "does-not-exist")));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(100, s.MicrophoneVolumePercent);
        Assert.Equal(AudioSource.Microphone, s.Mode);
    }

    [Fact]
    public void CorruptFileGivesDefaultsAndIsBackedUp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(100, s.MicrophoneVolumePercent);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.corrupt")));
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"MicrophoneVolumePercent\": 900, \"ApplicationVolumePercent\": -5, \"Mode\": \"Application\"}");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(200, s.MicrophoneVolumePercent);
        Assert.Equal(0, s.ApplicationVolumePercent);
        Assert.Equal(AudioSource.Application, s.Mode);
    }
}
