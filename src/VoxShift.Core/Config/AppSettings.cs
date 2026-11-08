using System.Text.Json.Serialization;
using VoxShift.Core.Audio;
using VoxShift.Core.Soundboard;

namespace VoxShift.Core.Config;

/// <summary>Everything that is remembered between application runs.</summary>
public sealed class AppSettings
{
    // 2: soundboard (v0.0.3), 3: soundboard monitoring (v0.0.4), 4: smart mixing, dock and VR Controller Mode (v0.0.5).
    // Older files load unchanged; new settings get their defaults (see Normalize).
    public const int CurrentSchemaVersion = 4;

    public const double DefaultSoundboardDockWidth = 420;
    public const double MinSoundboardDockWidth = 300;
    public const double MaxSoundboardDockWidth = 900;

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

    /// <summary>Legacy (v0.0.3/v0.0.4) expanded state of the embedded soundbar. Kept in sync with <see cref="SoundboardDockExpanded"/>.</summary>
    public bool SoundboardExpanded { get; set; } = true;

    /// <summary>The soundboard dock next to the main window is expanded (false = narrow strip).</summary>
    public bool SoundboardDockExpanded { get; set; } = true;

    /// <summary>The soundboard dock is shown next to the main window at all.</summary>
    public bool SoundboardDockVisible { get; set; } = true;

    /// <summary>Width of the expanded soundboard dock in device-independent pixels.</summary>
    public double SoundboardDockWidth { get; set; } = DefaultSoundboardDockWidth;

    /// <summary>Mute the microphone contribution while soundboard sounds play (VoxShift's mix only).</summary>
    public bool AutoMuteMicrophoneDuringSoundboard { get; set; } = true;

    /// <summary>In application mode, also transmit the microphone ("Talk over music").</summary>
    public bool TalkOverMusicEnabled { get; set; }

    /// <summary>Show the simplified VR Controller window instead of the full interface.</summary>
    public bool VrControllerModeEnabled { get; set; }

    public double? VrWindowLeft { get; set; }

    public double? VrWindowTop { get; set; }

    public double? VrWindowWidth { get; set; }

    public double? VrWindowHeight { get; set; }

    /// <summary>Height of the main window (device-independent pixels); null = default.</summary>
    public double? WindowHeight { get; set; }

    /// <summary>Also play soundboard sounds on your own default playback device (headphones).</summary>
    public bool SoundboardMonitorEnabled { get; set; } = true;

    /// <summary>Soundboard volume in your own headphones, independent of what VRChat hears.</summary>
    public int SoundboardMonitorVolumePercent { get; set; } = 70;

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
        SoundboardMonitorVolumePercent = VolumeMath.ClampPercent(SoundboardMonitorVolumePercent);
        SelectedSoundboardId ??= string.Empty;
        NormalizeSoundboards();
        if (WindowLeft is { } l && (double.IsNaN(l) || double.IsInfinity(l))) WindowLeft = null;
        if (WindowTop is { } t && (double.IsNaN(t) || double.IsInfinity(t))) WindowTop = null;
        WindowHeight = ValidSize(WindowHeight, 420, 4000);
        VrWindowLeft = Finite(VrWindowLeft);
        VrWindowTop = Finite(VrWindowTop);
        VrWindowWidth = ValidSize(VrWindowWidth, 360, 4000);
        VrWindowHeight = ValidSize(VrWindowHeight, 360, 4000);
        SoundboardDockWidth = double.IsFinite(SoundboardDockWidth)
            ? Math.Clamp(SoundboardDockWidth, MinSoundboardDockWidth, MaxSoundboardDockWidth)
            : DefaultSoundboardDockWidth;

        if (SchemaVersion < 4)
        {
            // Upgrade from v0.0.4 or older: the soundboard moves into the dock. People who already use the
            // soundboard get it visible and expanded; the new audio options start with their safe defaults.
            SoundboardDockExpanded = SoundboardExpanded || Soundboards.Count > 0;
            SoundboardDockVisible = true;
            SoundboardDockWidth = DefaultSoundboardDockWidth;
            AutoMuteMicrophoneDuringSoundboard = true;
            TalkOverMusicEnabled = false;
            VrControllerModeEnabled = false;
        }

        SoundboardExpanded = SoundboardDockExpanded; // keeps a downgrade to v0.0.4 consistent
        SchemaVersion = CurrentSchemaVersion;
        return this;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    private static double? ValidSize(double? value, double min, double max) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : null;

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
