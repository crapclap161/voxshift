using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class SincResamplerTests
{
    private static float[] Run(SincResampler r, float[] input, int inFrames, int outFramesPerCall, double ratio, int maxOut)
    {
        var output = new List<float>();
        int consumed = 0;
        var block = new float[outFramesPerCall * 2];
        while (output.Count / 2 < maxOut)
        {
            int need = r.InputFramesNeeded(outFramesPerCall, ratio);
            int give = Math.Min(need, inFrames - consumed);
            if (give > 0)
            {
                r.Write(input.AsSpan(consumed * 2), give);
                consumed += give;
            }

            int got = r.Read(block, outFramesPerCall, ratio);
            for (int i = 0; i < got * 2; i++) output.Add(block[i]);
            if (got == 0) break;
        }

        return output.ToArray();
    }

    [Fact]
    public void UnityRatio_PreservesSignal()
    {
        const int rate = 48000;
        var input = TestSignals.Sine(rate, 1000, rate / 2);
        var r = new SincResampler(1.0);
        var output = Run(r, input, rate / 2, 480, 1.0, rate / 2 - 100);

        // The history is pre-filled so that output frame n is centred on input frame n (zero delay).
        int delay = 0;
        double maxErr = 0;
        for (int i = 2000; i < output.Length / 2 - 100; i++)
        {
            maxErr = Math.Max(maxErr, Math.Abs(output[2 * i] - input[2 * (i - delay)]));
        }

        Assert.True(maxErr < 0.01, $"max error {maxErr}");
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(96000, 48000)]
    [InlineData(16000, 48000)]
    public void ConvertsRateWithoutChangingPitchOrLevel(int inRate, int outRate)
    {
        const double freq = 440;
        int inFrames = inRate; // one second
        var input = TestSignals.Sine(inRate, freq, inFrames, 0.5f);
        double ratio = (double)inRate / outRate;
        var r = new SincResampler(ratio);
        var output = Run(r, input, inFrames, 512, ratio, outRate - 2000);

        var steady = output.AsSpan(2000 * 2);
        Assert.InRange(TestSignals.EstimateFrequency(steady, outRate), freq - 1.5, freq + 1.5);
        Assert.InRange(TestSignals.Rms(steady), 0.5 / Math.Sqrt(2) * 0.97, 0.5 / Math.Sqrt(2) * 1.03);
    }

    [Fact]
    public void Downsampling_AttenuatesContentAboveOutputNyquist()
    {
        // 30 kHz at 96 kHz cannot be represented at 48 kHz and must be filtered, not aliased to 18 kHz.
        int inRate = 96000, outRate = 48000;
        var input = TestSignals.Sine(inRate, 30000, inRate / 2, 0.5f);
        var r = new SincResampler(2.0);
        var output = Run(r, input, inRate / 2, 512, 2.0, outRate / 2 - 1000);
        Assert.True(TestSignals.Rms(output.AsSpan(4000)) < 0.02, "aliasing not suppressed");
    }

    [Fact]
    public void DcGainIsUnity()
    {
        var input = TestSignals.Constant(10000, 0.25f, -0.25f);
        var r = new SincResampler(44100.0 / 48000);
        var output = Run(r, input, 10000, 256, 44100.0 / 48000, 9000);
        for (int i = 1000; i < 9000; i++)
        {
            Assert.Equal(0.25f, output[2 * i], 3);
            Assert.Equal(-0.25f, output[2 * i + 1], 3);
        }
    }
}
