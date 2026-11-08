namespace VoxShift.Core.Tests;

internal static class TestSignals
{
    public static float[] Sine(int rate, double freq, int frames, float amplitude = 0.5f, double phase = 0)
    {
        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / rate + phase));
            buf[2 * i] = v;
            buf[2 * i + 1] = v;
        }

        return buf;
    }

    public static float[] Constant(int frames, float l, float r)
    {
        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            buf[2 * i] = l;
            buf[2 * i + 1] = r;
        }

        return buf;
    }

    public static float Peak(ReadOnlySpan<float> s)
    {
        float p = 0;
        foreach (float v in s) p = Math.Max(p, Math.Abs(v));
        return p;
    }

    public static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (float v in s) sum += v * v;
        return Math.Sqrt(sum / Math.Max(1, s.Length));
    }

    /// <summary>Estimates the frequency of the left channel by counting positive zero crossings.</summary>
    public static double EstimateFrequency(ReadOnlySpan<float> stereo, int rate)
    {
        int frames = stereo.Length / 2;
        int first = -1, last = -1, crossings = 0;
        for (int i = 1; i < frames; i++)
        {
            if (stereo[2 * (i - 1)] < 0 && stereo[2 * i] >= 0)
            {
                if (first < 0) first = i;
                else crossings++;
                last = i;
            }
        }

        if (crossings == 0) return 0;
        return crossings * (double)rate / (last - first);
    }
}
