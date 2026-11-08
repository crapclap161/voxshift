using System.Text;

namespace VoxShift.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public interface ILog
{
    void Write(LogLevel level, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);

    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);

    public static void Warn(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Warning, message, ex);

    public static void Error(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Error, message, ex);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>
/// Small thread-safe file logger with size based rotation (current file + one ".old" file), so long
/// VRChat sessions never grow the log without bound. Logs stay on the local disk only.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private StreamWriter? _writer;
    private bool _disabled;

    public FileLog(string directory, string fileName = "VoxShift.log", long maxBytes = 1_000_000, LogLevel minimumLevel = LogLevel.Info)
    {
        Directory = directory;
        _path = Path.Combine(directory, fileName);
        _maxBytes = maxBytes;
        MinimumLevel = minimumLevel;
    }

    public string Directory { get; }

    public string FilePath => _path;

    public LogLevel MinimumLevel { get; set; }

    /// <summary>Default location: %LOCALAPPDATA%\VoxShift\logs.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxShift", "logs");

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level < MinimumLevel || _disabled) return;
        var sb = new StringBuilder(128);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
          .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
          .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ")
          .Append(message);
        if (exception is not null) sb.AppendLine().Append(exception);

        lock (_sync)
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(sb.ToString());
                _writer.Flush();
                if (_writer.BaseStream.Length > _maxBytes) Rotate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the audio engine down.
                _disabled = true;
            }
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null) return;
        System.IO.Directory.CreateDirectory(Directory);
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        string old = _path + ".old";
        File.Move(_path, old, overwrite: true);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
