using VoxShift.Core.Config;

namespace VoxShift.Core.Soundboard;

/// <summary>Outcome of adding files to a soundboard.</summary>
public sealed class ImportResult
{
    public SoundboardSet? Collection { get; init; }

    /// <summary>True when the import had to create the first collection automatically.</summary>
    public bool CreatedCollection { get; init; }

    public List<SoundEntry> Added { get; } = new();

    public List<string> Duplicates { get; } = new();

    public List<string> Unsupported { get; } = new();

    public List<string> Missing { get; } = new();

    public bool HasProblems => Duplicates.Count + Unsupported.Count + Missing.Count > 0;

    /// <summary>Short user-facing summary, e.g. "Added 3 sounds. Skipped 1 unsupported file (notes.txt)."</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added.Count > 0) parts.Add(Added.Count == 1 ? $"Added \"{Added[0].Name}\"." : $"Added {Added.Count} sounds.");
            if (Duplicates.Count > 0) parts.Add(Describe(Duplicates, "already on this soundboard"));
            if (Unsupported.Count > 0) parts.Add(Describe(Unsupported, "not a supported audio format") + " Supported: " + SoundboardLibrary.SupportedFormatsText + ".");
            if (Missing.Count > 0) parts.Add(Describe(Missing, "not found"));
            return parts.Count == 0 ? "No audio files found." : string.Join(" ", parts);
        }
    }

    private static string Describe(List<string> paths, string reason)
    {
        string names = string.Join(", ", paths.Take(3).Select(Path.GetFileName));
        if (paths.Count > 3) names += $" +{paths.Count - 3} more";
        return $"Skipped {paths.Count} ({reason}): {names}.";
    }
}

/// <summary>
/// All soundboard editing operations (collections, entries, imports, relinking) on top of <see cref="AppSettings"/>.
/// Pure logic without UI or audio dependencies, so it is fully unit tested. Entries reference the user's files by
/// absolute path; nothing here ever writes to, copies, moves or deletes those files.
/// </summary>
public sealed class SoundboardLibrary
{
    public const string DefaultCollectionName = "Soundboard";

    /// <summary>Upper bound for one import (e.g. a dropped folder) to keep the UI responsive.</summary>
    public const int MaxFilesPerImport = 500;

    private static readonly string[] Extensions = [".wav", ".mp3", ".flac", ".m4a", ".aac", ".wma"];

    private readonly AppSettings _settings;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, IEnumerable<string>> _listFiles;

    public SoundboardLibrary(
        AppSettings settings,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, IEnumerable<string>>? listFiles = null)
    {
        _settings = settings;
        _settings.Soundboards ??= new();
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
        _listFiles = listFiles ?? (dir => Directory.EnumerateFiles(dir));
    }

    public static IReadOnlyList<string> SupportedExtensions => Extensions;

    public static string SupportedFormatsText => "WAV, MP3, FLAC, M4A/AAC, WMA";

    /// <summary>File-dialog filter, e.g. "Audio files|*.wav;*.mp3;…".</summary>
    public static string FileDialogFilter =>
        "Audio files (" + SupportedFormatsText + ")|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|All files|*.*";

    public static bool IsSupportedFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SoundboardSet> Collections => _settings.Soundboards;

    public SoundboardSet? Selected =>
        _settings.Soundboards.FirstOrDefault(c => c.Id == _settings.SelectedSoundboardId) ?? _settings.Soundboards.FirstOrDefault();

    public SoundboardSet? Find(string collectionId) => _settings.Soundboards.FirstOrDefault(c => c.Id == collectionId);

    public bool FileExists(SoundEntry entry) => !string.IsNullOrWhiteSpace(entry.FilePath) && _fileExists(entry.FilePath);

    public void Select(string collectionId)
    {
        if (Find(collectionId) is not null) _settings.SelectedSoundboardId = collectionId;
    }

    /// <summary>Creates a collection with a unique name ("Soundboard", "Soundboard 2", …) and selects it.</summary>
    public SoundboardSet CreateCollection(string? name = null)
    {
        var collection = new SoundboardSet { Name = UniqueName(CleanName(name) ?? DefaultCollectionName) };
        _settings.Soundboards.Add(collection);
        _settings.SelectedSoundboardId = collection.Id;
        return collection;
    }

    /// <summary>Returns the selected collection, creating the default one if none exists yet.</summary>
    public SoundboardSet EnsureCollection(out bool created)
    {
        created = false;
        if (Selected is { } existing)
        {
            _settings.SelectedSoundboardId = existing.Id;
            return existing;
        }

        created = true;
        return CreateCollection(DefaultCollectionName);
    }

    public bool RenameCollection(string collectionId, string newName)
    {
        var collection = Find(collectionId);
        string? clean = CleanName(newName);
        if (collection is null || clean is null) return false;
        if (string.Equals(collection.Name, clean, StringComparison.Ordinal)) return true;
        collection.Name = UniqueName(clean, except: collection);
        return true;
    }

    /// <summary>Deletes a collection (only the references; audio files are untouched) and selects a neighbour.</summary>
    public bool DeleteCollection(string collectionId)
    {
        int index = _settings.Soundboards.FindIndex(c => c.Id == collectionId);
        if (index < 0) return false;
        _settings.Soundboards.RemoveAt(index);
        if (_settings.SelectedSoundboardId == collectionId)
        {
            _settings.SelectedSoundboardId = _settings.Soundboards.Count == 0
                ? string.Empty
                : _settings.Soundboards[Math.Min(index, _settings.Soundboards.Count - 1)].Id;
        }

        return true;
    }

    /// <summary>
    /// Adds audio files (and supported files inside dropped folders, non-recursive) to a collection, creating the
    /// default collection automatically when there is none. Unsupported, missing and duplicate files are skipped.
    /// Only absolute paths are stored.
    /// </summary>
    public ImportResult Import(IEnumerable<string> paths, string? collectionId = null)
    {
        SoundboardSet collection;
        bool created = false;
        if (collectionId is not null && Find(collectionId) is { } explicitCollection)
        {
            collection = explicitCollection;
        }
        else
        {
            collection = EnsureCollection(out created);
        }

        var result = new ImportResult { Collection = collection, CreatedCollection = created };
        var known = new HashSet<string>(collection.Sounds.Select(s => Normalize(s.FilePath)), StringComparer.OrdinalIgnoreCase);
        int processed = 0;

        foreach (string raw in ExpandFolders(paths))
        {
            if (processed++ >= MaxFilesPerImport) break;
            string full = Normalize(raw);
            if (!IsSupportedFile(full))
            {
                result.Unsupported.Add(full);
                continue;
            }

            if (!_fileExists(full))
            {
                result.Missing.Add(full);
                continue;
            }

            if (!known.Add(full))
            {
                result.Duplicates.Add(full);
                continue;
            }

            var entry = new SoundEntry { Name = DisplayNameFor(full), FilePath = full };
            collection.Sounds.Add(entry);
            result.Added.Add(entry);
        }

        _settings.SelectedSoundboardId = collection.Id;
        return result;
    }

    public bool RenameSound(string collectionId, string soundId, string newName)
    {
        var sound = FindSound(collectionId, soundId);
        string? clean = CleanName(newName);
        if (sound is null || clean is null) return false;
        sound.Name = clean;
        return true;
    }

    /// <summary>Removes the button only. The audio file on disk is never deleted.</summary>
    public bool RemoveSound(string collectionId, string soundId) =>
        Find(collectionId)?.Sounds.RemoveAll(s => s.Id == soundId) > 0;

    /// <summary>Points an entry at a new file (e.g. after the original was moved). Keeps its custom name.</summary>
    public RelinkResult Relink(string collectionId, string soundId, string newPath)
    {
        var collection = Find(collectionId);
        var sound = collection?.Sounds.FirstOrDefault(s => s.Id == soundId);
        if (collection is null || sound is null) return RelinkResult.NotFound;
        string full = Normalize(newPath);
        if (!IsSupportedFile(full)) return RelinkResult.Unsupported;
        if (!_fileExists(full)) return RelinkResult.Missing;
        if (collection.Sounds.Any(s => s.Id != soundId && string.Equals(Normalize(s.FilePath), full, StringComparison.OrdinalIgnoreCase)))
        {
            return RelinkResult.Duplicate;
        }

        sound.FilePath = full;
        return RelinkResult.Ok;
    }

    public SoundEntry? FindSound(string collectionId, string soundId) =>
        Find(collectionId)?.Sounds.FirstOrDefault(s => s.Id == soundId);

    /// <summary>"airhorn_loud.mp3" → "airhorn loud".</summary>
    public static string DisplayNameFor(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
        return name.Length == 0 ? "Sound" : name;
    }

    private IEnumerable<string> ExpandFolders(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (_directoryExists(path))
            {
                IEnumerable<string> files;
                try
                {
                    files = _listFiles(path).Where(IsSupportedFile).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string file in files) yield return file;
            }
            else
            {
                yield return path;
            }
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string? CleanName(string? name)
    {
        if (name is null) return null;
        string trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 60 ? trimmed[..60] : trimmed;
    }

    private string UniqueName(string baseName, SoundboardSet? except = null)
    {
        bool Taken(string n) => _settings.Soundboards.Any(c => !ReferenceEquals(c, except) && string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} {i}";
            if (!Taken(candidate)) return candidate;
        }
    }
}

public enum RelinkResult
{
    Ok,
    NotFound,
    Unsupported,
    Missing,
    Duplicate,
}
