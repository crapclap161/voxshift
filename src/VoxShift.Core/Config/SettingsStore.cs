using System.Text.Json;
using VoxShift.Core.Logging;

namespace VoxShift.Core.Config;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic (temp file + replace) so a crash
/// or power loss never leaves a truncated settings file; a corrupt file is backed up and replaced by defaults.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILog _log;
    private readonly object _sync = new();

    public SettingsStore(string directory, ILog? log = null)
    {
        Directory = directory;
        FilePath = Path.Combine(directory, "settings.json");
        _log = log ?? NullLog.Instance;
    }

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>Default location: %APPDATA%\VoxShift (override with VOXSHIFT_SETTINGS_DIR).</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable("VOXSHIFT_SETTINGS_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoxShift");
        }
    }

    /// <summary>
    /// Location used by pre-VoxShift builds ("VRChat Audio Switcher"). Only read, never written, so existing
    /// users keep their devices, application and volumes after upgrading.
    /// </summary>
    public static string LegacyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRChatAudioSwitcher");

    /// <summary>
    /// Copies settings.json from <paramref name="legacyDirectory"/> if this store has no settings yet.
    /// Returns true when a migration happened. Failures are logged and ignored (defaults are used).
    /// </summary>
    public bool TryMigrateFrom(string legacyDirectory)
    {
        lock (_sync)
        {
            try
            {
                string legacyFile = Path.Combine(legacyDirectory, "settings.json");
                if (File.Exists(FilePath) || !File.Exists(legacyFile)) return false;
                System.IO.Directory.CreateDirectory(Directory);
                File.Copy(legacyFile, FilePath, overwrite: false);
                _log.Info($"Imported settings from {legacyDirectory}.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Could not import settings from the previous version.", ex);
                return false;
            }
        }
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings().Normalize();
                string json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                return (settings ?? new AppSettings()).Normalize();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log.Warn($"Settings file could not be read ({ex.Message}); using defaults.");
                TryBackupCorruptFile();
                return new AppSettings().Normalize();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string json = JsonSerializer.Serialize(settings.Clone().Normalize(), JsonOptions);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("Could not save settings.", ex);
            }
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing else we can do; defaults are used.
        }
    }
}
