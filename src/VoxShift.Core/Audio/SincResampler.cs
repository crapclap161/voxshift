namespace VoxShift.Core.Audio;

/// <summary>
/// Band-limited (windowed-sinc, polyphase) stereo resampler whose conversion ratio may change on every
/// call without rebuilding filters. The variable ratio is what allows <see cref="SourceChannel"/> to
/// compensate for the clock drift between two independent audio devices (e.g. a USB microphone and the
/// virtual cable) so that hours-long sessions never accumulate latency or run dry.
/// </summary>
public sealed class SincResampler
{
    /// <summary>Half the number of filter taps; latency is roughly this many input frames.</summary>
    public const int HalfTaps = 16;
    private const int Taps = HalfTaps * 2;
    private const int Phases = 256;

    private readonly float[] _table; // (Phases + 1) rows of Taps coefficients
    private float[] _history = new float[4096 * 2];
    private int _historyFrames;
    private double _position; // fractional index (in frames) into _history of the next output sample

    /// <param name="nominalRatio">Expected input-rate / output-rate. Determines the anti-alias cutoff.</param>
    public SincResampler(double nominalRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nominalRatio);
        NominalRatio = nominalRatio;

        // When downsampling the cutoff must sit below the *output* Nyquist frequency.
        double cutoff = Math.Min(1.0, 1.0 / nominalRatio) * 0.93;
        _table = BuildTable(cutoff);
        Reset();
    }

    public double NominalRatio { get; }

    /// <summary>Input frames held in the history buffer that have not yet been fully consumed.</summary>
    public int BufferedFrames => Math.Max(0, _historyFrames - (int)Math.Floor(_position) - HalfTaps);

    public void Reset()
    {
        // Start with HalfTaps-1 frames of silence so the first output sample has full left context.
        _historyFrames = HalfTaps - 1;
        Array.Clear(_history, 0, _historyFrames * 2);
        _position = HalfTaps - 1;
    }

    /// <summary>How many more input frames must be written to be able to produce <paramref name="outputFrames"/>.</summary>
    public int InputFramesNeeded(int outputFrames, double ratio)
    {
        if (outputFrames <= 0) return 0;
        double lastPos = _position + (outputFrames - 1) * ratio;
        int needed = (int)Math.Floor(lastPos) + HalfTaps + 1 - _historyFrames;
        return Math.Max(0, needed);
    }

    public void Write(ReadOnlySpan<float> stereo, int frames)
    {
        if (frames <= 0) return;
        int required = (_historyFrames + frames) * 2;
        if (required > _history.Length)
        {
            Array.Resize(ref _history, Math.Max(required, _history.Length * 2));
        }

        stereo.Slice(0, frames * 2).CopyTo(_history.AsSpan(_historyFrames * 2));
        _historyFrames += frames;
    }

    /// <summary>
    /// Produces up to <paramref name="outputFrames"/> stereo frames using <paramref name="ratio"/> input frames
    /// per output frame. Returns the number of frames produced (fewer when input runs out).
    /// </summary>
    public int Read(Span<float> destination, int outputFrames, double ratio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratio);
        float[] h = _history;
        float[] table = _table;
        int produced = 0;

        while (produced < outputFrames)
        {
            int i = (int)Math.Floor(_position);
            if (i + HalfTaps >= _historyFrames) break; // not enough look-ahead

            double frac = _position - i;
            double phaseF = frac * Phases;
            int phase = (int)phaseF;
            float mix = (float)(phaseF - phase);
            int rowA = phase * Taps;
            int rowB = rowA + Taps;

            int start = (i - HalfTaps + 1) * 2;
            float l = 0f, r = 0f;
            for (int t = 0; t < Taps; t++)
            {
                float c = table[rowA + t] + (table[rowB + t] - table[rowA + t]) * mix;
                int idx = start + 2 * t;
                l += h[idx] * c;
                r += h[idx + 1] * c;
            }

            destination[2 * produced] = l;
            destination[2 * produced + 1] = r;
            produced++;
            _position += ratio;
        }

        Compact();
        return produced;
    }

    private void Compact()
    {
        int keepFrom = (int)Math.Floor(_position) - HalfTaps + 1;
        if (keepFrom <= 0) return;
        keepFrom = Math.Min(keepFrom, _historyFrames);
        int remaining = _historyFrames - keepFrom;
        if (remaining > 0)
        {
            Array.Copy(_history, keepFrom * 2, _history, 0, remaining * 2);
        }

        _historyFrames = remaining;
        _position -= keepFrom;
    }

    private static float[] BuildTable(double cutoff)
    {
        var table = new float[(Phases + 1) * Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int t = 0; t < Taps; t++)
            {
                // Tap t multiplies input sample (i - HalfTaps + 1 + t); its distance from the output position.
                double x = t - HalfTaps + 1 - frac;
                double v = cutoff * Sinc(cutoff * x) * Window(x / HalfTaps);
                table[p * Taps + t] = (float)v;
                sum += v;
            }

            // Normalise for exact unity gain at DC regardless of phase.
            for (int t = 0; t < Taps; t++) table[p * Taps + t] = (float)(table[p * Taps + t] / sum);
        }

        return table;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9) return 1.0;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    /// <summary>Blackman window over [-1, 1].</summary>
    private static double Window(double x)
    {
        if (x <= -1 || x >= 1) return 0;
        return 0.42 + 0.5 * Math.Cos(Math.PI * x) + 0.08 * Math.Cos(2 * Math.PI * x);
    }
}
