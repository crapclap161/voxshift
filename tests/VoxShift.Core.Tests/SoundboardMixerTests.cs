using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class SoundboardMixerTests
{
    private const int Rate = 48000;
    private const int Block = 480;

    private static AudioClip Dc(float value, int frames) => new(TestSignals.Constant(frames, value, value), Rate);

    private static float[] Render(SoundboardMixer mixer, int blocks = 1)
    {
        var all = new List<float>();
        for (int b = 0; b < blocks; b++)
        {
            var buf = new float[Block * 2];
            mixer.MixInto(buf, Block);
            all.AddRange(buf);
        }

        return all.ToArray();
    }

    [Fact]
    public void PlaysClipAndReportsState()
    {
        var mixer = new SoundboardMixer(Rate) { Volume = 1f };
        mixer.Play("horn", Dc(0.5f, Rate));

        Assert.True(mixer.IsPlaying("horn"));
        var o = Render(mixer, 2);
        Assert.Equal(0.5f, o[^1], 3);
        var progress = new List<VoiceProgress>();
        mixer.CopyProgress(progress);
        var p = Assert.Single(progress);
        Assert.InRange(p.Progress, 0.01, 0.03);
    }

    [Fact]
    public void ClipEndsAndVoiceIsReleased()
    {
        var mixer = new SoundboardMixer(Rate) { Volume = 1f };
        mixer.Play("short", Dc(0.5f, 100));
        Render(mixer, 1);
        Assert.Equal(0, mixer.ActiveCount);
        Assert.False(mixer.IsPlaying("short"));
        Assert.All(Render(mixer), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void MultipleSoundsAreMixedSimultaneously()
    {
        var mixer = new SoundboardMixer(Rate) { Volume = 1f };
        mixer.Play("a", Dc(0.25f, Rate));
        mixer.Play("b", Dc(0.125f, Rate));
        mixer.Play("a", Dc(0.25f, Rate)); // same sound twice overlaps
        var o = Render(mixer, 2);
        Assert.Equal(0.625f, o[^1], 3);
        Assert.Equal(3, mixer.ActiveCount);
    }

    [Fact]
    public void StopFadesOutOnlyThatSound()
    {
        var mixer = new SoundboardMixer(Rate) { Volume = 1f };
        mixer.Play("a", Dc(0.25f, Rate));
        mixer.Play("b", Dc(0.5f, Rate));
        Render(mixer, 2);

        mixer.Stop("a");
        Assert.False(mixer.IsPlaying("a"));
        var fade = Render(mixer, 1);
        // No click: the first sample after Stop is still close to the previous level.
        Assert.InRange(fade[0], 0.70f, 0.76f);
        var after = Render(mixer, 2);
        Assert.Equal(0.5f, after[^1], 3);
        Assert.True(mixer.IsPlaying("b"));
    }

    [Fact]
    public void StopAllSilencesEverythingAndReleasesVoices()
    {
        var mixer = new SoundboardMixer(Rate) { Volume = 1f };
        mixer.Play("a", Dc(0.25f, Rate));
        mixer.Play("b", Dc(0.5f, Rate));
        Render(mixer, 2);

        mixer.StopAll();
        Render(mixer, 2); // fade-out
        Assert.Equal(0, mixer.ActiveCount);
        Assert.All(Render(mixer), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void VolumeScalesOutputSmoothly()
    {
        var mixer = new SoundboardMixer(Rate) { Volume = 0.5f };
        mixer.Play("a", Dc(0.5f, Rate * 2));
        var o = Render(mixer, 4);
        Assert.Equal(0.25f, o[^1], 3);

        mixer.Volume = 2f;
        var ramp = Render(mixer, 1);
        Assert.True(ramp[0] < 0.3f, "volume must ramp, not jump");
        var settled = Render(mixer, 10);
        Assert.Equal(1.0f, settled[^1], 2);
    }

    [Fact]
    public void FadeInPreventsClickAtStart()
    {
        var mixer = new SoundboardMixer(Rate) { Volume = 1f };
        mixer.Play("a", Dc(0.9f, Rate));
        var o = Render(mixer);
        Assert.True(o[0] < 0.05f, $"first sample {o[0]}");
    }

    [Fact]
    public void VoiceLimitStopsOldestVoice()
    {
        var mixer = new SoundboardMixer(Rate) { Volume = 1f };
        for (int i = 0; i < SoundboardMixer.MaxVoices + 5; i++) mixer.Play("s" + i, Dc(0.01f, Rate));
        Render(mixer, 3);
        Assert.True(mixer.ActiveCount <= SoundboardMixer.MaxVoices);
    }

    [Fact]
    public void WrongSampleRateIsRejectedAndRateChangeClearsVoices()
    {
        var mixer = new SoundboardMixer(Rate);
        Assert.Throws<ArgumentException>(() => mixer.Play("a", new AudioClip(new float[200], 44100)));
        mixer.Play("a", Dc(0.5f, Rate));
        mixer.SetOutputRate(44100);
        Assert.Equal(0, mixer.ActiveCount);
    }
}
