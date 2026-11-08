using System.Reflection;

namespace VoxShift;

/// <summary>Product name and version as compiled into the executable (from Directory.Build.props).</summary>
public static class AppInfo
{
    public const string Name = "VoxShift";

    /// <summary>Semantic version without build metadata, e.g. "0.0.1".</summary>
    public static string Version { get; } = ReadVersion();

    public static string DisplayName => $"{Name} {Version}";

    private static string ReadVersion()
    {
        string? informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        int plus = informational.IndexOf('+', System.StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
