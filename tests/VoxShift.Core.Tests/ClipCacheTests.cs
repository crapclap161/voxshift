using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class ClipCacheTests
{
    private const int Rate = 48000;

    /// <summary>A clip of <paramref name="frames"/> stereo frames (8 bytes per frame once decoded).</summary>
    private static AudioClip Clip(int frames) => new(new float[frames * 2], Rate);

    [Fact]
    public async Task LoadsOnceAndSharesTheResult()
    {
        var cache = new ClipCache(1024 * 1024);
        int loads = 0;
        AudioClip Load()
        {
            Interlocked.Increment(ref loads);
            return Clip(100);
        }

        var first = await cache.GetOrAddAsync("a", Load);
        var second = await cache.GetOrAddAsync("a", Load);

        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Equal(1, cache.Count);
        Assert.Equal(first.SizeInBytes, cache.TotalBytes);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneKeyShareASingleLoad()
    {
        var cache = new ClipCache(1024 * 1024);
        int loads = 0;
        using var release = new ManualResetEventSlim(false);
        AudioClip Load()
        {
            Interlocked.Increment(ref loads);
            release.Wait(TimeSpan.FromSeconds(10));
            return Clip(100);
        }

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.GetOrAddAsync("same", Load))).ToArray();
        await WaitFor(() => Volatile.Read(ref loads) == 1);
        await Task.Delay(50); // give every request the chance to (wrongly) start its own load
        release.Set();
        var clips = await Task.WhenAll(tasks);

        Assert.Equal(1, loads);
        Assert.All(clips, c => Assert.Same(clips[0], c));
    }

    [Fact]
    public async Task RequestReturnsBeforeTheLoadHasRun()
    {
        // The request comes from the UI thread, so it must never decode inline. A loader that blocks until the
        // caller releases it would deadlock (and then report a completed task) if it ran inside GetOrAddAsync.
        var cache = new ClipCache(1024 * 1024);
        using var release = new ManualResetEventSlim(false);
        bool releasedInTime = false;
        var task = cache.GetOrAddAsync("a", () =>
        {
            releasedInTime = release.Wait(TimeSpan.FromSeconds(10));
            return Clip(10);
        });

        Assert.False(task.IsCompleted);
        release.Set();
        await task;
        Assert.True(releasedInTime);
    }

    [Fact]
    public async Task FailedLoadIsNotCachedSoTheNextRequestRetries()
    {
        var cache = new ClipCache(1024 * 1024);
        int attempts = 0;
        AudioClip Load()
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("broken file");
            return Clip(100);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrAddAsync("a", Load));
        Assert.Equal(0, cache.Count);

        // Regression: the very next click must decode again instead of replaying the cached failure.
        var clip = await cache.GetOrAddAsync("a", Load);
        Assert.Equal(100, clip.Frames);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task EvictsLeastRecentlyUsedClipsWhenOverBudget()
    {
        long clipBytes = Clip(1000).SizeInBytes;
        var cache = new ClipCache(clipBytes * 2); // room for two clips

        var a = await cache.GetOrAddAsync("a", () => Clip(1000));
        await cache.GetOrAddAsync("b", () => Clip(1000));
        await cache.GetOrAddAsync("a", () => throw new InvalidOperationException("a must still be cached")); // touch a
        await cache.GetOrAddAsync("c", () => Clip(1000));
        await WaitFor(() => cache.Count == 2);

        Assert.True(cache.TotalBytes <= clipBytes * 2);
        int reloads = 0;
        Assert.Same(a, await cache.GetOrAddAsync("a", () => Clip(1)));
        await cache.GetOrAddAsync("b", () =>
        {
            reloads++;
            return Clip(1000);
        });
        Assert.Equal(1, reloads); // b was the least recently used one and had to be decoded again
    }

    [Fact]
    public async Task AClipLargerThanTheWholeBudgetIsStillUsable()
    {
        var cache = new ClipCache(16);
        var clip = await cache.GetOrAddAsync("big", () => Clip(1000));
        Assert.Equal(1000, clip.Frames);
        await WaitFor(() => cache.Count == 1); // the newest clip is never evicted, so a click always finds its sound
    }

    [Fact]
    public async Task InvalidateForgetsEveryKeyWithThePrefixOnly()
    {
        var cache = new ClipCache(1024 * 1024);
        await cache.GetOrAddAsync(@"C:\sounds\horn.wav|10|1|48000", () => Clip(10));
        await cache.GetOrAddAsync(@"C:\sounds\horn.wav|10|1|44100", () => Clip(10));
        await cache.GetOrAddAsync(@"C:\sounds\horn.wav.bak|10|1|48000", () => Clip(10));

        cache.Invalidate(@"c:\SOUNDS\horn.wav|"); // Windows paths are case-insensitive

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task ClearingWhileALoadIsRunningDoesNotResurrectTheEntry()
    {
        var cache = new ClipCache(1024 * 1024);
        using var release = new ManualResetEventSlim(false);
        var task = cache.GetOrAddAsync("a", () =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return Clip(100);
        });

        cache.Clear(); // e.g. application shutdown while a sound is still decoding
        release.Set();
        var clip = await task; // the caller that asked still gets its clip …

        Assert.Equal(100, clip.Frames);
        await Task.Delay(50);
        Assert.Equal(0, cache.Count); // … but nothing is put back into a cleared cache
        Assert.Equal(0, cache.TotalBytes);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
