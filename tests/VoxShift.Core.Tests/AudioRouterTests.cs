using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class AudioRouterTests
{
    private const int Rate = 48000;
    private const int Block = 480;

    /// <summary>Mic carries +0.25 DC on both channels, app carries -0.5 DC, so every output sample is attributable.</summary>
    private static void Feed(AudioRouter router)
    {
        router.Microphone.Write(TestSignals.Constant(Block, 0.25f, 0.25f), Block, Rate);
        router.Application.Write(TestSignals.Constant(Block, -0.5f, -0.5f), Block, Rate);
    }

    private static float[] Step(AudioRouter router)
    {
        Feed(router);
        var outBuf = new float[Block * 2];
        router.Render(outBuf, Block);
        return outBuf;
    }

    [Fact]
    public void MicrophoneMode_TransmitsOnlyMicrophone()
    {
        var router = new AudioRouter(Rate, AudioSource.Microphone);
        for (int i = 0; i < 10; i++) Step(router);
        for (int i = 0; i < 50; i++)
        {
            var o = Step(router);
            Assert.All(o, v => Assert.True(v >= 0f, $"application audio leaked: {v}"));
            Assert.Equal(0.25f, o[^1], 2);
        }
    }

    [Fact]
    public void ApplicationMode_TransmitsOnlyApplication()
    {
        var router = new AudioRouter(Rate, AudioSource.Application);
        for (int i = 0; i < 10; i++) Step(router);
        for (int i = 0; i < 50; i++)
        {
            var o = Step(router);
            Assert.All(o, v => Assert.True(v <= 0f, $"microphone audio leaked: {v}"));
            Assert.Equal(-0.5f, o[^1], 2);
        }
    }

    [Fact]
    public void Switching_NeverMixesSourcesAndFadesSmoothly()
    {
        var router = new AudioRouter(Rate, AudioSource.Microphone);
        for (int i = 0; i < 20; i++) Step(router);

        router.RequestedSource = AudioSource.Application;
        var samples = new List<float>();
        for (int i = 0; i < 20; i++) samples.AddRange(Step(router));

        Assert.Equal(AudioSource.Application, router.ActiveSource);

        // Once the first negative (app) sample appears, no positive (mic) sample may follow.
        int firstApp = samples.FindIndex(v => v < 0f);
        Assert.True(firstApp > 0);
        Assert.DoesNotContain(samples.Skip(firstApp), v => v > 0f);

        // No sample-to-sample jump bigger than a fade step allows (no click).
        float maxJump = 0;
        for (int i = 2; i < samples.Count; i += 2) maxJump = Math.Max(maxJump, Math.Abs(samples[i] - samples[i - 2]));
        Assert.True(maxJump < 0.01f, $"discontinuity {maxJump}");

        // And switching back works.
        router.RequestedSource = AudioSource.Microphone;
        float[] last = Array.Empty<float>();
        for (int i = 0; i < 20; i++) last = Step(router);
        Assert.Equal(AudioSource.Microphone, router.ActiveSource);
        Assert.Equal(0.25f, last[^1], 2);
    }

    [Fact]
    public void VolumeChangesOutputLevelIndependently()
    {
        var router = new AudioRouter(Rate, AudioSource.Microphone);
        router.Microphone.TargetGain = 2f;
        router.Application.TargetGain = 0.5f;
        float[] o = Array.Empty<float>();
        for (int i = 0; i < 30; i++) o = Step(router);
        Assert.Equal(0.5f, o[^1], 2); // 0.25 * 2

        router.RequestedSource = AudioSource.Application;
        for (int i = 0; i < 30; i++) o = Step(router);
        Assert.Equal(-0.25f, o[^1], 2); // -0.5 * 0.5
    }

    [Fact]
    public void HighGainIsLimitedBelowFullScale()
    {
        var router = new AudioRouter(Rate, AudioSource.Application);
        router.Application.TargetGain = 2f;
        float max = 0;
        for (int i = 0; i < 50; i++)
        {
            router.Microphone.Write(TestSignals.Constant(Block, 0f, 0f), Block, Rate);
            router.Application.Write(TestSignals.Sine(Rate, 200, Block, 0.95f, i * 2 * Math.PI * 200 * Block / Rate), Block, Rate);
            var outBuf = new float[Block * 2];
            router.Render(outBuf, Block);
            max = Math.Max(max, TestSignals.Peak(outBuf));
        }

        Assert.True(max <= 0.8915f, $"peak {max}");
        Assert.True(max > 0.8f);
    }

    [Fact]
    public void MissingSource_ProducesSilenceNotNoise()
    {
        var router = new AudioRouter(Rate, AudioSource.Application);
        var outBuf = new float[Block * 2];
        for (int i = 0; i < 10; i++)
        {
            router.Microphone.Write(TestSignals.Constant(Block, 0.25f, 0.25f), Block, Rate);
            router.Render(outBuf, Block);
            Assert.All(outBuf, v => Assert.Equal(0f, v));
        }
    }
}
