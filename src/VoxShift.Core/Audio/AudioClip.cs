namespace VoxShift.Core.Audio;

/// <summary>
/// A fully decoded sound held in memory as interleaved stereo float at a fixed sample rate. Immutable once
/// created, so the render thread can read it without locks or allocations.
/// </summary>
public sealed class AudioClip
{
    public AudioClip(float[] stereoSamples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(stereoSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (stereoSamples.Length % 2 != 0) throw new ArgumentException("Stereo buffer must have an even length.", nameof(stereoSamples));
        Samples = stereoSamples;
        SampleRate = sampleRate;
    }

    public float[] Samples { get; }

    public int SampleRate { get; }

    public int Frames => Samples.Length / 2;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    /// <summary>Approximate memory used by the decoded samples.</summary>
    public long SizeInBytes => (long)Samples.Length * sizeof(float);

    /// <summary>Returns this clip converted to <paramref name="targetRate"/> (band-limited), or itself if equal.</summary>
    public AudioClip ResampleTo(int targetRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);
        if (targetRate == SampleRate || Frames == 0) return Frames == 0 ? new AudioClip([], targetRate) : this;

        double ratio = (double)SampleRate / targetRate;
        var resampler = new SincResampler(ratio);
        int expected = (int)Math.Ceiling(Frames / ratio) + 8;
        var output = new float[expected * 2];
        int written = 0;
        int consumed = 0;
        const int Block = 4096;
        var block = new float[Block * 2];

        // Feed silence after the end so the filter tail is flushed and the last samples are produced.
        int padFrames = SincResampler.HalfTaps * 2;
        var silence = new float[padFrames * 2];
        bool padded = false;
        while (written < expected)
        {
            int need = resampler.InputFramesNeeded(Block, ratio);
            if (need > 0)
            {
                int available = Frames - consumed;
                if (available > 0)
                {
                    int take = Math.Min(need, available);
                    resampler.Write(Samples.AsSpan(consumed * 2, take * 2), take);
                    consumed += take;
                }
                else if (!padded)
                {
                    resampler.Write(silence, padFrames);
                    padded = true;
                }
            }

            int got = resampler.Read(block, Math.Min(Block, expected - written), ratio);
            if (got == 0) break;
            block.AsSpan(0, got * 2).CopyTo(output.AsSpan(written * 2));
            written += got;
        }

        int frames = Math.Min(written, (int)Math.Round(Frames / ratio));
        return new AudioClip(output.AsSpan(0, frames * 2).ToArray(), targetRate);
    }
}
