using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VoxShift.Core.Apps;
using VoxShift.Core.Logging;

namespace VoxShift.Audio.Apps;

/// <summary>An application that can be selected for capture.</summary>
public sealed record AudioApplicationInfo(
    string ExeName,
    string DisplayName,
    int RootProcessId,
    string? WindowTitle,
    bool HasAudioSession,
    bool IsPlaying,
    int ProcessCount)
{
    public bool IsVrchat => string.Equals(ExeName, "vrchat", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Lists running programs that have an audio session or a visible window, grouped into applications
/// (all processes of e.g. Chrome are one entry, captured together as a process tree).
/// </summary>
public static class AudioApplicationEnumerator
{
    // Shell/system processes that own windows or sessions but are never what the user wants to capture.
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "audiodg", "dwm", "csrss", "winlogon", "svchost", "explorer",
        "applicationframehost", "textinputhost", "shellexperiencehost", "startmenuexperiencehost",
        "searchhost", "searchapp", "systemsettings", "lockapp", "widgets", "widgetservice",
        "taskmgr", "sihost", "ctfmon", "runtimebroker", "nvcontainer", "voxshift",
    };

    public static IReadOnlyList<AudioApplicationInfo> Enumerate(MMDeviceEnumerator enumerator, ILog log)
    {
        var snapshot = NativeProcess.Snapshot();
        var tree = new ProcessTree(snapshot);
        int self = Environment.ProcessId;
        var selfTree = tree.GetTree(self);

        var sessions = GetSessionProcesses(enumerator, log);
        var windows = NativeProcess.GetWindowTitles();

        var candidates = sessions.Keys.Concat(windows.Keys)
            .Where(pid => pid > 4 && tree.Contains(pid) && !selfTree.Contains(pid))
            .Distinct()
            .ToList();

        var result = new List<AudioApplicationInfo>();
        foreach (var (root, members) in tree.GroupByApplication(candidates))
        {
            var rootEntry = tree.Get(root);
            if (rootEntry is null) continue;
            string exe = ProcessTree.NormalizeExeName(rootEntry.Value.ExeName);
            if (Ignored.Contains(exe) || selfTree.Contains(root)) continue;

            var allPids = tree.GetTree(root);
            bool hasSession = allPids.Any(sessions.ContainsKey);
            bool playing = allPids.Any(p => sessions.TryGetValue(p, out bool active) && active);

            string? title = null;
            if (windows.TryGetValue(root, out var rootTitles)) title = rootTitles.FirstOrDefault();
            title ??= members.Where(windows.ContainsKey).SelectMany(p => windows[p]).FirstOrDefault();
            title ??= allPids.Where(windows.ContainsKey).SelectMany(p => windows[p]).FirstOrDefault();

            result.Add(new AudioApplicationInfo(
                exe,
                GetDisplayName(root, rootEntry.Value.ExeName),
                root,
                title,
                hasSession,
                playing,
                allPids.Count(p => tree.Get(p) is { } e && string.Equals(e.ExeName, rootEntry.Value.ExeName, StringComparison.OrdinalIgnoreCase))));
        }

        return result
            .OrderByDescending(a => a.IsPlaying)
            .ThenByDescending(a => a.HasAudioSession)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Finds the process tree root to capture for an executable name (e.g. after the program was restarted).
    /// </summary>
    public static int? ResolveRoot(string exeName, MMDeviceEnumerator? enumerator, ILog log)
    {
        var tree = new ProcessTree(NativeProcess.Snapshot());
        IReadOnlyCollection<int>? preferred = null;
        if (enumerator is not null) preferred = GetSessionProcesses(enumerator, log).Keys.ToList();
        return tree.FindRootByExeName(exeName, preferred);
    }

    /// <summary>Process ids that currently have an active (playing) audio session on any active render device.</summary>
    public static IReadOnlyList<int> GetActiveAudioProcessIds(MMDeviceEnumerator enumerator, ILog log) =>
        GetSessionProcesses(enumerator, log).Where(s => s.Value).Select(s => s.Key).ToList();

    /// <summary>pid → whether the session is currently active (playing) on any active render device.</summary>
    private static Dictionary<int, bool> GetSessionProcesses(MMDeviceEnumerator enumerator, ILog log)
    {
        var result = new Dictionary<int, bool>();
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        var manager = device.AudioSessionManager;
                        manager.RefreshSessions();
                        var sessions = manager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            using var session = sessions[i];
                            if (session.IsSystemSoundsSession) continue;
                            int pid = (int)session.GetProcessID;
                            if (pid <= 4) continue;
                            bool active = session.State == AudioSessionState.AudioSessionStateActive;
                            result[pid] = result.TryGetValue(pid, out bool prev) ? prev || active : active;
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Debug($"Could not read audio sessions of a device: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("Enumerating audio sessions failed.", ex);
        }

        return result;
    }

    private static string GetDisplayName(int pid, string exeFile)
    {
        string fallback = Path.GetFileNameWithoutExtension(exeFile);
        try
        {
            string? path = NativeProcess.GetImagePath(pid);
            if (path is null) return fallback;
            var info = FileVersionInfo.GetVersionInfo(path);
            string? description = info.FileDescription?.Trim();
            if (string.IsNullOrEmpty(description)) description = info.ProductName?.Trim();
            return string.IsNullOrEmpty(description) ? fallback : description;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
