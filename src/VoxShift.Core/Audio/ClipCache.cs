namespace VoxShift.Core.Audio;

/// <summary>
/// Thread-safe cache of decoded <see cref="AudioClip"/>s with a memory budget. Each key is loaded at most once at a
/// time on a thread-pool thread; concurrent requests for the same key share that load. When the decoded audio exceeds
/// the budget, the least recently used clips are forgotten (playing voices hold their own reference, so eviction never
/// cuts a sound). A failed load is not cached: the next request simply tries again.
/// </summary>
public sealed class ClipCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _budgetBytes;
    private long _clock;

    /// <param name="budgetBytes">Upper bound for decoded audio kept in memory across all cached clips.</param>
    public ClipCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budgetBytes = budgetBytes;
    }

    /// <summary>Number of cached or currently loading clips.</summary>
    public int Count
    {
        get
        {
            lock (_sync) return _entries.Count;
        }
    }

    /// <summary>Decoded bytes held by the clips that finished loading.</summary>
    public long TotalBytes
    {
        get
        {
            lock (_sync) return _entries.Values.Sum(e => e.Size);
        }
    }

    /// <summary>
    /// Returns the clip for <paramref name="key"/>, loading it with <paramref name="load"/> on the thread pool when it
    /// is not cached yet. Never runs <paramref name="load"/> on the calling thread.
    /// </summary>
    public Task<AudioClip> GetOrAddAsync(string key, Func<AudioClip> load)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);

        Entry entry;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.LastUsed = ++_clock;
                return existing.Task;
            }

            entry = new Entry(new Task<AudioClip>(load), ++_clock);
            _entries[key] = entry;
        }

        entry.Task.ContinueWith(_ => OnLoaded(key, entry), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        entry.Task.Start(TaskScheduler.Default);
        return entry.Task;
    }

    /// <summary>Forgets every clip whose key starts with <paramref name="keyPrefix"/> (e.g. all rates of one file).</summary>
    public void Invalidate(string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);
        lock (_sync)
        {
            foreach (string key in _entries.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_sync) _entries.Clear();
    }

    private void OnLoaded(string key, Entry entry)
    {
        lock (_sync)
        {
            // Invalidated or cleared while loading: the result belongs to nobody any more.
            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;

            if (!entry.Task.IsCompletedSuccessfully)
            {
                _entries.Remove(key); // not cached, so the next request retries (the file may have been fixed)
                return;
            }

            entry.Size = entry.Task.Result.SizeInBytes;
            Trim();
        }
    }

    /// <summary>Evicts least recently used clips until the budget holds again; the newest clip always stays.</summary>
    private void Trim()
    {
        long total = 0;
        foreach (var e in _entries.Values) total += e.Size;
        if (total <= _budgetBytes) return;

        foreach (var (key, entry) in _entries.Where(e => e.Value.Task.IsCompleted).OrderBy(e => e.Value.LastUsed).ToList())
        {
            if (total <= _budgetBytes || _entries.Count <= 1) break;
            _entries.Remove(key);
            total -= entry.Size;
        }
    }

    private sealed class Entry(Task<AudioClip> task, long lastUsed)
    {
        public Task<AudioClip> Task { get; } = task;

        public long LastUsed { get; set; } = lastUsed;

        public long Size { get; set; }
    }
}
