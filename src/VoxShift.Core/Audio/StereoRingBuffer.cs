namespace VoxShift.Core.Audio;

/// <summary>
/// Fixed-capacity FIFO of interleaved stereo float frames. When a write would overflow the buffer the
/// oldest frames are discarded, which bounds latency if the consumer stalls. Not thread safe on its own;
/// <see cref="SourceChannel"/> serialises access.
/// </summary>
public sealed class StereoRingBuffer
{
    private readonly float[] _data;
    private int _readFrame;
    private int _count;

    public StereoRingBuffer(int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        CapacityFrames = capacityFrames;
        _data = new float[capacityFrames * 2];
    }

    public int CapacityFrames { get; }

    /// <summary>Number of frames currently buffered.</summary>
    public int Count => _count;

    /// <summary>Total number of frames dropped because of overflow since creation.</summary>
    public long OverflowFrames { get; private set; }

    public void Write(ReadOnlySpan<float> stereo, int frames)
    {
        if (frames <= 0) return;
        if (frames > CapacityFrames)
        {
            // Only the newest CapacityFrames can be kept.
            int skip = frames - CapacityFrames;
            OverflowFrames += skip;
            stereo = stereo.Slice(skip * 2);
            frames = CapacityFrames;
        }

        int free = CapacityFrames - _count;
        if (frames > free) Discard(frames - free, countAsOverflow: true);

        int writeFrame = (_readFrame + _count) % CapacityFrames;
        int first = Math.Min(frames, CapacityFrames - writeFrame);
        stereo.Slice(0, first * 2).CopyTo(_data.AsSpan(writeFrame * 2));
        if (first < frames)
        {
            stereo.Slice(first * 2, (frames - first) * 2).CopyTo(_data.AsSpan(0));
        }

        _count += frames;
    }

    /// <summary>Reads up to <paramref name="frames"/> frames; returns the number actually read.</summary>
    public int Read(Span<float> destination, int frames)
    {
        int n = Math.Min(frames, _count);
        if (n <= 0) return 0;
        int first = Math.Min(n, CapacityFrames - _readFrame);
        _data.AsSpan(_readFrame * 2, first * 2).CopyTo(destination);
        if (first < n)
        {
            _data.AsSpan(0, (n - first) * 2).CopyTo(destination.Slice(first * 2));
        }

        _readFrame = (_readFrame + n) % CapacityFrames;
        _count -= n;
        return n;
    }

    /// <summary>Drops up to <paramref name="frames"/> of the oldest frames.</summary>
    public int Discard(int frames) => Discard(frames, countAsOverflow: false);

    private int Discard(int frames, bool countAsOverflow)
    {
        int n = Math.Clamp(frames, 0, _count);
        _readFrame = (_readFrame + n) % CapacityFrames;
        _count -= n;
        if (countAsOverflow) OverflowFrames += n;
        return n;
    }

    public void Clear()
    {
        _readFrame = 0;
        _count = 0;
    }
}
