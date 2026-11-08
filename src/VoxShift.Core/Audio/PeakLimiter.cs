namespace VoxShift.Core.Audio;

/// <summary>
/// Stereo-linked peak limiter with instantaneous attack and exponential release. Because the envelope
/// follows every sample peak immediately, the output can never exceed <see cref="Ceiling"/> – this is
/// what prevents harsh digital clipping when a volume slider is set to 200 %.
/// </summary>
public sealed class PeakLimiter
{
    private float _envelope;
    private float _releaseCoefficient;

    public PeakLimiter(int sampleRate, float ceiling = 0.891f /* -1 dBFS */, double releaseMs = 150)
    {
        Ceiling = ceiling;
        ReleaseMs = releaseMs;
        SetSampleRate(sampleRate);
    }

    public float Ceiling { get; }

    public double ReleaseMs { get; }

    /// <summary>Current gain reduction as linear factor (1 = no reduction).</summary>
    public float CurrentGain => _envelope > Ceiling ? Ceiling / _envelope : 1f;

    public void SetSampleRate(int sampleRate)
    {
        _releaseCoefficient = (float)Math.Exp(-1.0 / (ReleaseMs / 1000.0 * sampleRate));
    }

    public void Reset() => _envelope = 0f;

    public void Process(Span<float> stereo, int frames)
    {
        float env = _envelope;
        float rel = _releaseCoefficient;
        float ceiling = Ceiling;
        for (int f = 0; f < frames; f++)
        {
            int i = 2 * f;
            float l = stereo[i];
            float r = stereo[i + 1];
            if (float.IsNaN(l) || float.IsInfinity(l)) l = 0f;
            if (float.IsNaN(r) || float.IsInfinity(r)) r = 0f;

            float peak = Math.Max(Math.Abs(l), Math.Abs(r));
            env = peak > env ? peak : env * rel + peak * (1f - rel);
            if (env < peak) env = peak;

            float gain = env > ceiling ? ceiling / env : 1f;
            stereo[i] = Math.Clamp(l * gain, -1f, 1f);
            stereo[i + 1] = Math.Clamp(r * gain, -1f, 1f);
        }

        _envelope = env < 1e-9f ? 0f : env;
    }
}
