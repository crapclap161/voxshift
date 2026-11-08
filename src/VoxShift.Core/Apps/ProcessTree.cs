namespace VoxShift.Core.Apps;

/// <summary>A process as seen in a Toolhelp snapshot.</summary>
public readonly record struct ProcessEntry(int ProcessId, int ParentProcessId, string ExeName);

/// <summary>
/// Pure logic for mapping processes to the "application" that should be captured.
/// Multi-process programs (Chrome, Edge, Firefox, Spotify, Discord …) render audio from a helper process
/// that is a descendant of the main process with the same executable name. Windows process-loopback
/// capture can include a whole process tree, so we always capture from the top-most ancestor that shares
/// the executable name; that captures every tab/window of the program.
/// </summary>
public sealed class ProcessTree
{
    private readonly Dictionary<int, ProcessEntry> _byId;

    public ProcessTree(IEnumerable<ProcessEntry> processes)
    {
        _byId = new Dictionary<int, ProcessEntry>();
        foreach (var p in processes)
        {
            _byId[p.ProcessId] = p;
        }
    }

    public bool Contains(int pid) => _byId.ContainsKey(pid);

    public ProcessEntry? Get(int pid) => _byId.TryGetValue(pid, out var e) ? e : null;

    /// <summary>
    /// Walks up the parent chain while the parent has the same executable name and returns the
    /// top-most such process id. Guards against PID reuse cycles.
    /// </summary>
    public int GetApplicationRoot(int pid)
    {
        if (!_byId.TryGetValue(pid, out var current)) return pid;
        var visited = new HashSet<int> { pid };
        while (current.ParentProcessId != 0
               && current.ParentProcessId != current.ProcessId
               && _byId.TryGetValue(current.ParentProcessId, out var parent)
               && string.Equals(parent.ExeName, current.ExeName, StringComparison.OrdinalIgnoreCase)
               && visited.Add(parent.ProcessId))
        {
            current = parent;
        }

        return current.ProcessId;
    }

    /// <summary>Returns all descendants (any depth) of <paramref name="rootPid"/> including itself.</summary>
    public IReadOnlySet<int> GetTree(int rootPid)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var p in _byId.Values)
        {
            if (p.ParentProcessId == p.ProcessId) continue;
            if (!children.TryGetValue(p.ParentProcessId, out var list))
            {
                list = new List<int>();
                children[p.ParentProcessId] = list;
            }

            list.Add(p.ProcessId);
        }

        var result = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootPid);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (!result.Add(id)) continue;
            if (children.TryGetValue(id, out var kids))
            {
                foreach (var k in kids) stack.Push(k);
            }
        }

        return result;
    }

    /// <summary>
    /// Groups candidate process ids by application root and returns one root per group.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> GroupByApplication(IEnumerable<int> candidatePids)
    {
        var groups = new Dictionary<int, List<int>>();
        foreach (int pid in candidatePids)
        {
            int root = GetApplicationRoot(pid);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }

            if (!list.Contains(pid)) list.Add(pid);
        }

        return groups.ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Value);
    }

    /// <summary>
    /// Finds the best root process for an executable name (used to re-attach after the program restarts).
    /// Prefers roots that appear in <paramref name="preferredPids"/> (e.g. processes with an audio session).
    /// </summary>
    public int? FindRootByExeName(string exeName, IReadOnlyCollection<int>? preferredPids = null)
    {
        string normalized = NormalizeExeName(exeName);
        var roots = _byId.Values
            .Where(p => string.Equals(NormalizeExeName(p.ExeName), normalized, StringComparison.OrdinalIgnoreCase))
            .Select(p => GetApplicationRoot(p.ProcessId))
            .Distinct()
            .ToList();
        if (roots.Count == 0) return null;
        if (preferredPids is { Count: > 0 })
        {
            foreach (int root in roots)
            {
                if (preferredPids.Any(pp => GetApplicationRoot(pp) == root)) return root;
            }
        }

        return roots.Min();
    }

    /// <summary>"Spotify.exe" / "spotify" → "spotify".</summary>
    public static string NormalizeExeName(string exeName)
    {
        string name = exeName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }
}
