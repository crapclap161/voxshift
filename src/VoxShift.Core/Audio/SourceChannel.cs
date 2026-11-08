namespace VoxShift.Core.Audio;

/// <summary>
/// One input of the router (microphone or application). The capture thread pushes audio in at the
/// device's own rate with <see cref="Write"/>; the render thread pulls it out at the output rate with
/// <see cref="Read"/>. In between sit a jitter buffer and a drift-compensating resampler that keeps the
/// buffer at <see cref="TargetLatencyMs"/>, plus a smoothed gain stage (no zipper noise when dragging a slider).
/// </summary>
public sealed class SourceChannel
{
    private const int RingSeconds = 2;
    private const double MaxDriftCorrection = 0.002; // ±0.2 % (≈ ±3.5 cents) – inaudible
    private const double DriftCorrectionPerSecondOfError = 0.25;

    private readonly object _sync = new();
    private StereoRingBuffer? _ring;
    private SincResampler? _resampler;
    private float[] _scratch = new float[4096 * 2];
    private int _inputRate;
    private int _outputRate;
    private bool _prebuffering = true;
    private double _fillAverage;
    private float _currentGain;
    private float _gainCoefficient;

    private volatile float _targetGain = 1f;
    private int _peakBits; // float bits, read-and-reset by the UI meter
    private long _lastWriteTick = long.MinValue;
    private long _underruns;

    public SourceChannel(string name, int outputRate)
    {
        Name = name;
        SetOutputRate(outputRate);
        _currentGain = _targetGain;
    }

    public string Name { get; }

    /// <summary>Target amount of buffered audio. Small enough for conversation, large enough to absorb scheduling jitter.</summary>
    public double TargetLatencyMs { get; init; } = 40;

    /// <summary>When more than this is buffered (e.g. after a stall) the excess is dropped at once.</summary>
    public double MaxLatencyMs { get; init; } = 200;

    /// <summary>Linear gain applied to this source (0..2). Changes are smoothed over ~10 ms.</summary>
    public float TargetGain
    {
        get => _targetGain;
        set => _targetGain = Math.Clamp(value, 0f, 4f);
    }

    public int InputRate
    {
        get { lock (_sync) return _inputRate; }
    }

    /// <summary>Number of times the render side ran out of audio after playback had started.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Frames currently waiting in the jitter buffer (input rate).</summary>
    public int BufferedFrames
    {
        get { lock (_sync) return _ring?.Count ?? 0; }
    }

    /// <summary>Monotonic millisecond tick of the last successful <see cref="Write"/>; used for "no signal" display.</summary>
    public long LastWriteTick => Interlocked.Read(ref _lastWriteTick);

    public void SetOutputRate(int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        lock (_sync)
        {
            _outputRate = outputRate;
            // One-pole smoothing with a ~10 ms time constant.
            _gainCoefficient = (float)(1.0 - Math.Exp(-1.0 / (0.010 * outputRate)));
            if (_inputRate > 0) _resampler = new SincResampler((double)_inputRate / _outputRate);
            _prebuffering = true;
        }
    }

    /// <summary>
    /// Called from the capture thread with interleaved stereo float frames at <paramref name="inputRate"/>.
    /// </summary>
    public void Write(ReadOnlySpan<float> stereo, int frames, int inputRate)
    {
        if (frames <= 0) return;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);

        // Meter shows the level *after* the volume slider, i.e. what would be transmitted.
        float peak = 0f;
        ReadOnlySpan<float> samples = stereo.Slice(0, frames * 2);
        for (int i = 0; i < samples.Length; i++)
        {
            float a = Math.Abs(samples[i]);
            if (a > peak) peak = a;
        }

        UpdatePeak(peak * _targetGain);

        lock (_sync)
        {
            if (inputRate != _inputRate || _ring is null)
            {
                _inputRate = inputRate;
                _ring = new StereoRingBuffer(inputRate * RingSeconds);
                _resampler = new SincResampler((double)inputRate / _outputRate);
                _prebuffering = true;
            }

            _ring.Write(samples, frames);
        }

        Interlocked.Exchange(ref _lastWriteTick, Environment.TickCount64);
    }

    /// <summary>
    /// Render-thread pull for the *active* source. Always fills <paramref name="frames"/> stereo frames
    /// (padding with silence on underrun). Returns how many frames contained real audio.
    /// </summary>
    public int Read(Span<float> destination, int frames)
    {
        Span<float> outSpan = destination.Slice(0, frames * 2);
        int produced = 0;

        lock (_sync)
        {
            if (_ring is not null && _resampler is not null)
            {
                produced = ReadLocked(outSpan, frames);
            }
        }

        if (produced < frames) outSpan.Slice(produced * 2).Clear();

        // Smoothed gain (applied to the whole block so the ramp continues through silence too).
        float g = _currentGain;
        float target = _targetGain;
        float k = _gainCoefficient;
        for (int f = 0; f < frames; f++)
        {
            g += (target - g) * k;
            outSpan[2 * f] *= g;
            outSpan[2 * f + 1] *= g;
        }

        _currentGain = Math.Abs(g - target) < 1e-6f ? target : g;
        return produced;
    }

    private int ReadLocked(Span<float> outSpan, int frames)
    {
        var ring = _ring!;
        var resampler = _resampler!;
        double targetFrames = _inputRate * TargetLatencyMs / 1000.0;
        double maxFrames = _inputRate * MaxLatencyMs / 1000.0;
        int fill = ring.Count + resampler.BufferedFrames;

        if (_prebuffering)
        {
            if (fill < targetFrames) return 0;
            _prebuffering = false;
            _fillAverage = fill;
        }

        if (fill > maxFrames)
        {
            ring.Discard((int)(fill - targetFrames));
            fill = ring.Count + resampler.BufferedFrames;
            _fillAverage = fill;
        }

        _fillAverage += (fill - _fillAverage) * 0.02;
        double errorSeconds = (_fillAverage - targetFrames) / _inputRate;
        double correction = Math.Clamp(errorSeconds * DriftCorrectionPerSecondOfError, -MaxDriftCorrection, MaxDriftCorrection);
        double ratio = (double)_inputRate / _outputRate * (1.0 + correction);

        int needed = resampler.InputFramesNeeded(frames, ratio);
        if (needed > 0)
        {
            EnsureScratch(needed);
            int got = ring.Read(_scratch, needed);
            resampler.Write(_scratch, got);
        }

        int produced = resampler.Read(outSpan, frames, ratio);
        if (produced < frames)
        {
            // Ran dry: go back to prebuffering so we recover with a full jitter buffer instead of crackling.
            _prebuffering = true;
            Interlocked.Increment(ref _underruns);
        }

        return produced;
    }

    /// <summary>
    /// Called on every render cycle for the source that is *not* transmitted. Keeps only the most recent
    /// <see cref="TargetLatencyMs"/> of audio so that switching to it is instant, and so that nothing stale
    /// (or old) could ever be transmitted later.
    /// </summary>
    public void Idle()
    {
        lock (_sync)
        {
            if (_ring is null) return;
            int target = (int)(_inputRate * TargetLatencyMs / 1000.0);
            if (_ring.Count > target) _ring.Discard(_ring.Count - target);
        }
    }

    /// <summary>Prepares the channel to become the transmitted source (fresh resampler state).</summary>
    public void Activate()
    {
        lock (_sync)
        {
            _resampler?.Reset();
            _prebuffering = true;
        }

        _currentGain = _targetGain;
    }

    /// <summary>Drops all buffered audio, e.g. when the capture device is stopped or replaced.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _ring?.Clear();
            _resampler?.Reset();
            _prebuffering = true;
        }
    }

    /// <summary>Returns the highest (post-gain) peak since the previous call and resets it.</summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    private void UpdatePeak(float value)
    {
        int newBits = BitConverter.SingleToInt32Bits(value);
        while (true)
        {
            int current = Volatile.Read(ref _peakBits);
            if (BitConverter.Int32BitsToSingle(current) >= value) return;
            if (Interlocked.CompareExchange(ref _peakBits, newBits, current) == current) return;
        }
    }

    private void EnsureScratch(int frames)
    {
        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
    }
}
