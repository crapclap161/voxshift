using System.Text.Json.Serialization;
using VoxShift.Core.Audio;
using VoxShift.Core.Soundboard;

namespace VoxShift.Core.Config;

/// <summary>Everything that is remembered between application runs.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2; // 2: soundboard (v0.0.3); older files load with defaults

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Endpoint id of the physical microphone; empty = Windows default communications/console microphone.</summary>
    public string MicrophoneDeviceId { get; set; } = string.Empty;

    /// <summary>Friendly name, used to re-find the device if its id changed (e.g. different USB port).</summary>
    public string MicrophoneDeviceName { get; set; } = string.Empty;

    /// <summary>Endpoint id of the virtual cable's playback side; empty = auto-detect.</summary>
    public string OutputDeviceId { get; set; } = string.Empty;

    public string OutputDeviceName { get; set; } = string.Empty;

    /// <summary>Executable name of the captured application (e.g. "spotify"), re-resolved to a process at runtime.</summary>
    public string ApplicationExeName { get; set; } = string.Empty;

    public string ApplicationDisplayName { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AudioSource Mode { get; set; } = AudioSource.Microphone;

    public int MicrophoneVolumePercent { get; set; } = 100;

    public int ApplicationVolumePercent { get; set; } = 60;

    public bool AlwaysOnTop { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool HotkeyEnabled { get; set; }

    /// <summary>Global hotkey that toggles between the two modes, e.g. "Ctrl+Alt+M".</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+M";

    /// <summary>Soundboard collections. Only file references (absolute paths) are stored, never audio data.</summary>
    public List<SoundboardSet> Soundboards { get; set; } = new();

    public string SelectedSoundboardId { get; set; } = string.Empty;

    public int SoundboardVolumePercent { get; set; } = 80;

    public bool SoundboardExpanded { get; set; } = true;

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    /// <summary>Clamps every value into its valid range (protects against hand-edited files).</summary>
    public AppSettings Normalize()
    {
        MicrophoneDeviceId ??= string.Empty;
        MicrophoneDeviceName ??= string.Empty;
        OutputDeviceId ??= string.Empty;
        OutputDeviceName ??= string.Empty;
        ApplicationExeName ??= string.Empty;
        ApplicationDisplayName ??= string.Empty;
        ToggleHotkey = string.IsNullOrWhiteSpace(ToggleHotkey) ? "Ctrl+Alt+M" : ToggleHotkey;
        MicrophoneVolumePercent = VolumeMath.ClampPercent(MicrophoneVolumePercent);
        ApplicationVolumePercent = VolumeMath.ClampPercent(ApplicationVolumePercent);
        if (!Enum.IsDefined(Mode)) Mode = AudioSource.Microphone;
        SoundboardVolumePercent = VolumeMath.ClampPercent(SoundboardVolumePercent);
        SelectedSoundboardId ??= string.Empty;
        NormalizeSoundboards();
        if (WindowLeft is { } l && (double.IsNaN(l) || double.IsInfinity(l))) WindowLeft = null;
        if (WindowTop is { } t && (double.IsNaN(t) || double.IsInfinity(t))) WindowTop = null;
        SchemaVersion = CurrentSchemaVersion;
        return this;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private void NormalizeSoundboards()
    {
        Soundboards ??= new();
        Soundboards.RemoveAll(c => c is null);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in Soundboards)
        {
            if (string.IsNullOrWhiteSpace(collection.Id) || !ids.Add(collection.Id))
            {
                collection.Id = Guid.NewGuid().ToString("N");
                ids.Add(collection.Id);
            }

            collection.Name = string.IsNullOrWhiteSpace(collection.Name) ? "Soundboard" : collection.Name.Trim();
            collection.Sounds ??= new();
            collection.Sounds.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.FilePath));
            var soundIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sound in collection.Sounds)
            {
                if (string.IsNullOrWhiteSpace(sound.Id) || !soundIds.Add(sound.Id))
                {
                    sound.Id = Guid.NewGuid().ToString("N");
                    soundIds.Add(sound.Id);
                }

                if (string.IsNullOrWhiteSpace(sound.Name)) sound.Name = SoundboardLibrary.DisplayNameFor(sound.FilePath);
            }
        }

        if (Soundboards.Count > 0 && !Soundboards.Any(c => c.Id == SelectedSoundboardId))
        {
            SelectedSoundboardId = Soundboards[0].Id;
        }
    }
}
