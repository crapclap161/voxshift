using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

/// <summary>Soundboard as an extra layer on top of the "one source at a time" router.</summary>
public class SoundboardRoutingTests
{
    private const int Rate = 48000;
    private const int Block = 480;

    // Distinct DC levels make every source identifiable in the output:
    // microphone +0.25, application -0.5, soundboard +0.0625.
    private static float[] Step(AudioRouter router)
    {
        router.Microphone.Write(TestSignals.Constant(Block, 0.25f, 0.25f), Block, Rate);
        router.Application.Write(TestSignals.Constant(Block, -0.5f, -0.5f), Block, Rate);
        var o = new float[Block * 2];
        router.Render(o, Block);
        return o;
    }

    private static AudioClip Sound(int frames) => new(TestSignals.Constant(frames, 0.0625f, 0.0625f), Rate);

    [Fact]
    public void MicrophonePlusSoundboard()
    {
        var router = new AudioRouter(Rate, AudioSource.Microphone);
        router.Soundboard.Volume = 1f;
        for (int i = 0; i < 10; i++) Step(router);
        router.Soundboard.Play("x", Sound(Rate));
        float[] o = [];
        for (int i = 0; i < 5; i++) o = Step(router);
        Assert.Equal(0.3125f, o[^1], 0.001f); // 0.25 mic + 0.0625 sound, no application
    }

    [Fact]
    public void ApplicationPlusSoundboardWithoutMicrophoneLeak()
    {
        var router = new AudioRouter(Rate, AudioSource.Application);
        router.Soundboard.Volume = 1f;
        for (int i = 0; i < 10; i++) Step(router);
        router.Soundboard.Play("x", Sound(Rate));
        for (int i = 0; i < 20; i++)
        {
            var o = Step(router);
            // -0.5 app + 0.0625 sound = -0.4375; any microphone leak would push it towards -0.1875.
            if (i >= 2) Assert.All(o, v => Assert.InRange(v, -0.44f, -0.43f));
        }
    }

    [Fact]
    public void SoundboardKeepsPlayingThroughModeSwitch()
    {
        var router = new AudioRouter(Rate, AudioSource.Microphone);
        router.Soundboard.Volume = 1f;
        for (int i = 0; i < 10; i++) Step(router);
        router.Soundboard.Play("x", Sound(Rate));
        Step(router);

        router.RequestedSource = AudioSource.Application;
        var samples = new List<float>();
        for (int i = 0; i < 20; i++) samples.AddRange(Step(router));

        // During the hand-over (both sources faded to zero) only the soundboard is audible.
        Assert.Contains(samples, v => Math.Abs(v - 0.0625f) < 0.002f);
        Assert.True(router.Soundboard.IsPlaying("x"));
        Assert.Equal(-0.4375f, samples[^1], 3);

        // Sources are still never mixed with each other: once application audio appears (output below the
        // soundboard level), no microphone contribution (output above the soundboard level) may follow.
        int firstApp = samples.FindIndex(v => v < 0.0625f - 0.001f);
        Assert.True(firstApp > 0);
        Assert.DoesNotContain(samples.Skip(firstApp), v => v > 0.0625f + 0.001f);
    }

    [Fact]
    public void SoundboardGoesThroughLimiterAndOutputMeter()
    {
        var router = new AudioRouter(Rate, AudioSource.Microphone);
        router.Soundboard.Volume = 2f;
        router.Soundboard.Play("loud", new AudioClip(TestSignals.Sine(Rate, 200, Rate, 0.95f), Rate));
        float max = 0;
        for (int i = 0; i < 30; i++)
        {
            var o = new float[Block * 2]; // no mic/app input at all
            router.Render(o, Block);
            max = Math.Max(max, TestSignals.Peak(o));
        }

        Assert.True(max <= 0.8915f, $"limiter must catch the soundboard, peak {max}");
        Assert.True(router.TakeOutputPeak() > 0.5f, "output meter must include the soundboard");
    }
}
