namespace VoxShift.Core.Soundboard;

/// <summary>
/// A named set of sounds. Persisted in settings.json. It only stores *references* (absolute paths) to the
/// user's audio files: files are never copied, moved, modified or deleted by VoxShift.
/// </summary>
public sealed class SoundboardSet
{
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "Soundboard";

    public List<SoundEntry> Sounds { get; set; } = new();

    internal static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>One soundboard button: a display name and the absolute path of the original audio file.</summary>
public sealed class SoundEntry
{
    public string Id { get; set; } = SoundboardSet.NewId();

    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path of the user's original file, played in place.</summary>
    public string FilePath { get; set; } = string.Empty;
}
