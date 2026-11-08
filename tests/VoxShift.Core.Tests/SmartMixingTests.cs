using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

/// <summary>
/// v0.0.5 smart mixing: Talk over music and automatic microphone muting during soundboard playback.
/// Every source carries a distinct DC level so each output sample is attributable:
/// microphone +0.25, application -0.5, soundboard +0.0625.
/// </summary>
public class SmartMixingTests
{
    private const int Rate = 48000;
    private const int Block = 480; // 10 ms
    private const float Mic = 0.25f;
    private const float App = -0.5f;
    private const float Snd = 0.0625f;
    private const float Tol = 0.002f;

    private static float[] Step(AudioRouter router, bool feedMic = true, bool feedApp = true)
    {
        if (feedMic) router.Microphone.Write(TestSignals.Constant(Block, Mic, Mic), Block, Rate);
        if (feedApp) router.Application.Write(TestSignals.Constant(Block, App, App), Block, Rate);
        var o = new float[Block * 2];
        router.Render(o, Block);
        return o;
    }

    private static List<float> Run(AudioRouter router, int blocks)
    {
        var all = new List<float>();
        for (int i = 0; i < blocks; i++) all.AddRange(Step(router));
        return all;
    }

    private static AudioClip Sound(double seconds) => new(TestSignals.Constant((int)(Rate * seconds), Snd, Snd), Rate);

    private static AudioRouter Router(AudioSource mode, bool talkOver, bool autoMute)
    {
        var router = new AudioRouter(Rate, mode) { TalkOverMusic = talkOver, AutoMuteMicrophone = autoMute };
        router.Soundboard.Volume = 1f;
        Run(router, 15); // fill jitter buffers, settle gates
        return router;
    }

    // ---- the behaviour matrix from the specification -----------------------------------------------

    public static TheoryData<AudioSource, bool, bool, bool, float> Matrix => new()
    {
        // mode, talk over music, soundboard playing, auto-mute, expected steady-state output
        { AudioSource.Microphone, false, false, true, Mic },
        { AudioSource.Microphone, false, true, true, Snd },
        { AudioSource.Microphone, false, true, false, Mic + Snd },
        { AudioSource.Application, false, false, true, App },
        { AudioSource.Application, false, true, true, App + Snd },
        { AudioSource.Application, true, false, true, App + Mic },
        { AudioSource.Application, true, true, true, App + Snd },
        { AudioSource.Application, true, true, false, App + Mic + Snd },
        // remaining combinations
        { AudioSource.Microphone, true, false, true, Mic },
        { AudioSource.Microphone, true, true, true, Snd },
        { AudioSource.Microphone, true, true, false, Mic + Snd },
        { AudioSource.Application, false, true, false, App + Snd },
        { AudioSource.Application, true, false, false, App + Mic },
        { AudioSource.Microphone, false, false, false, Mic },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void BehaviourMatrix(AudioSource mode, bool talkOver, bool playing, bool autoMute, float expected)
    {
        var router = Router(mode, talkOver, autoMute);
        if (playing) router.Soundboard.Play("x", Sound(2));
        var o = Run(router, 10);
        Assert.All(o.Skip(o.Count - Block * 2), v => Assert.Equal(expected, v, Tol));
    }

    [Fact]
    public void MixPolicyMatchesTheMatrix()
    {
        foreach (AudioSource mode in Enum.GetValues<AudioSource>())
        {
            foreach (bool talk in new[] { false, true })
            {
                foreach (bool auto in new[] { false, true })
                {
                    foreach (bool playing in new[] { false, true })
                    {
                        var d = MixPolicy.Decide(new MixInputs(mode, talk, auto, playing));
                        bool micWanted = mode == AudioSource.Microphone || talk;
                        Assert.Equal(mode == AudioSource.Application, d.Application);
                        Assert.Equal(micWanted && !(auto && playing), d.Microphone);
                        Assert.Equal(micWanted && auto && playing, d.MicrophoneSuppressed);
                    }
                }
            }
        }
    }

    // ---- auto-mute timing ---------------------------------------------------------------------------

    [Fact]
    public void MicrophoneIsMutedWithinOneFadeOfPlaybackStart()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.Soundboard.Play("x", Sound(1));
        var first = Step(router);

        // Within the first 8 ms fade the microphone is gone; nothing above "soundboard only" remains afterwards.
        int fadeFrames = (int)(AudioRouter.FadeMs * Rate / 1000) + 2;
        for (int f = fadeFrames; f < Block; f++) Assert.Equal(Snd, first[2 * f], Tol);
        Assert.True(router.IsMicrophoneSuppressed);
        Assert.False(router.IsMicrophoneOpen);
    }

    [Fact]
    public void MicrophoneReturnsAutomaticallyWhenTheSoundEnds()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.Soundboard.Play("x", Sound(0.2));
        Run(router, 22); // sound (200 ms) has finished
        Assert.Equal(0, router.Soundboard.ActiveCount);

        // Restored after the short release hold + soft fade, never later.
        int blocks = (int)Math.Ceiling((AudioRouter.AutoMuteReleaseHoldMs + AudioRouter.MicrophoneRestoreFadeMs) / 10) + 2;
        var o = Run(router, blocks);
        Assert.Equal(Mic, o[^1], Tol);
        Assert.False(router.IsMicrophoneSuppressed);
        Assert.True(router.IsMicrophoneOpen);
    }

    [Fact]
    public void MicrophoneStaysMutedUntilTheLastOverlappingSoundFinishes()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.Soundboard.Play("short", Sound(0.1));
        Run(router, 3);
        router.Soundboard.Play("long", Sound(0.6));

        // The short sound ends after ~100 ms; the long one keeps the microphone muted.
        for (int i = 0; i < 40; i++)
        {
            var o = Step(router);
            // Two sounds add up to 2 × 0.0625; any microphone contribution would add up to +0.25 on top.
            Assert.All(o, v => Assert.True(v < 2 * Snd + 0.01f, $"microphone came back too early: {v}"));
        }

        Run(router, 40);
        Assert.Equal(Mic, Step(router)[^1], Tol);
    }

    [Fact]
    public void StopAllRestoresTheMicrophone()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.Soundboard.Play("a", Sound(5));
        router.Soundboard.Play("b", Sound(5));
        Run(router, 5);
        Assert.True(router.IsMicrophoneSuppressed);

        router.Soundboard.StopAll();
        Run(router, 20);
        Assert.Equal(0, router.Soundboard.ActiveCount);
        Assert.Equal(Mic, Step(router)[^1], Tol);
    }

    [Fact]
    public void StoppingOneOfTwoSoundsKeepsTheMicrophoneMuted()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.Soundboard.Play("a", Sound(5));
        router.Soundboard.Play("b", Sound(5));
        Run(router, 5);

        router.Soundboard.Stop("a");
        var o = Run(router, 30);
        Assert.True(router.IsMicrophoneSuppressed);
        Assert.Equal(Snd, o[^1], Tol);

        router.Soundboard.Stop("b");
        Run(router, 20);
        Assert.Equal(Mic, Step(router)[^1], Tol);
    }

    [Fact]
    public void InterruptedPlaybackNeverLeavesTheMicrophoneMuted()
    {
        // The output device stopped (voices dropped), or the device rate changed while sounds played.
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.Soundboard.Play("a", Sound(5));
        Run(router, 5);
        router.Soundboard.Clear();
        Run(router, 20);
        Assert.Equal(Mic, Step(router)[^1], Tol);

        router.Soundboard.Play("b", Sound(5));
        Run(router, 5);
        router.SetOutputRate(Rate); // device re-opened
        Run(router, 25);
        Assert.Equal(Mic, Step(router)[^1], Tol);
    }

    [Fact]
    public void QuickRetriggersDoNotLetTheMicrophoneFlickerInBetween()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        var samples = new List<float>();
        for (int n = 0; n < 4; n++)
        {
            router.Soundboard.Play("click" + n, Sound(0.05));
            samples.AddRange(Run(router, 8)); // 80 ms: next click comes inside the 100 ms release hold
        }

        int start = (int)(AudioRouter.FadeMs * Rate / 1000 + 2) * 2;
        Assert.All(samples.Skip(start), v => Assert.True(v <= Snd + Tol, $"microphone leaked between clicks: {v}"));
    }

    [Fact]
    public void MuteAndRestoreAreClickFree()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        var samples = new List<float>();
        samples.AddRange(Run(router, 2));

        // A real sound file ends in silence; give the test clip a 10 ms tail so only the router's gates are measured.
        var clip = TestSignals.Constant((int)(Rate * 0.15), Snd, Snd);
        int tail = Rate / 100, frames = clip.Length / 2;
        for (int f = frames - tail; f < frames; f++) clip[2 * f] = clip[2 * f + 1] = Snd * (frames - f) / tail;
        router.Soundboard.Play("x", new AudioClip(clip, Rate));
        samples.AddRange(Run(router, 40));

        float maxJump = 0;
        for (int i = 2; i < samples.Count; i += 2) maxJump = Math.Max(maxJump, Math.Abs(samples[i] - samples[i - 2]));

        // The largest jump is the soundboard's own 3 ms fade-in / 12 ms fade-out, far below an audible click.
        Assert.True(maxJump < 0.01f, $"discontinuity {maxJump}");
    }

    // ---- settings changes while sounds play ----------------------------------------------------------

    [Fact]
    public void DisablingAutoMuteDuringPlaybackBringsTheMicrophoneBackImmediately()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.Soundboard.Play("x", Sound(5));
        Run(router, 5);
        router.AutoMuteMicrophone = false;
        var o = Run(router, 6);
        Assert.Equal(Mic + Snd, o[^1], Tol);

        router.AutoMuteMicrophone = true;
        o = Run(router, 3);
        Assert.Equal(Snd, o[^1], Tol);
    }

    [Fact]
    public void SwitchingToApplicationModeWhileASoundPlaysNeverRestoresTheMicrophone()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.Soundboard.Play("x", Sound(0.2));
        Run(router, 5);
        router.RequestedSource = AudioSource.Application; // user changed mode during playback

        var o = Run(router, 60); // sound ends, hold and restore window pass
        Assert.All(o, v => Assert.True(v <= Snd + Tol, $"microphone leaked into application mode: {v}"));
        Assert.Equal(App, o[^1], Tol);
        Assert.False(router.IsMicrophoneOpen);
    }

    [Fact]
    public void TurningTalkOverMusicOffDuringPlaybackKeepsTheMicrophoneOffAfterwards()
    {
        var router = Router(AudioSource.Application, talkOver: true, autoMute: true);
        router.Soundboard.Play("x", Sound(0.2));
        Run(router, 5);
        router.TalkOverMusic = false;
        var o = Run(router, 60);
        Assert.Equal(App, o[^1], Tol);
        Assert.All(o, v => Assert.True(v <= App + Snd + Tol, $"microphone leaked: {v}"));
    }

    [Fact]
    public void MusicKeepsPlayingWhileTheMicrophoneIsAutoMuted()
    {
        var router = Router(AudioSource.Application, talkOver: true, autoMute: true);
        Assert.Equal(App + Mic, Step(router)[^1], Tol);

        router.Soundboard.Play("airhorn", Sound(0.3));
        var during = Run(router, 20);
        Assert.Equal(App + Snd, during[^1], Tol);
        Assert.True(router.IsApplicationOpen);
        Assert.All(during, v => Assert.True(v < 0f, "application audio must never drop out"));

        var after = Run(router, 30);
        Assert.Equal(App + Mic, after[^1], Tol);
        Assert.All(after, v => Assert.True(v < 0f, "application audio must never drop out"));
    }

    // ---- talk over music ----------------------------------------------------------------------------

    [Fact]
    public void TalkOverMusicOff_NeverLeaksTheMicrophoneIntoApplicationMode()
    {
        var router = Router(AudioSource.Application, talkOver: false, autoMute: true);
        router.Soundboard.Play("x", Sound(0.1));
        var o = Run(router, 60);
        Assert.All(o, v => Assert.True(v <= App + Snd + Tol, $"microphone leaked: {v}"));
        Assert.Equal(App, o[^1], Tol);
    }

    [Fact]
    public void TalkOverMusicOn_NeverLeaksApplicationAudioIntoMicrophoneMode()
    {
        var router = Router(AudioSource.Microphone, talkOver: true, autoMute: false);
        var o = Run(router, 40);
        Assert.All(o, v => Assert.True(v >= 0f, $"application audio leaked: {v}"));
        Assert.Equal(Mic, o[^1], Tol);
    }

    [Fact]
    public void TalkOverMusic_ModeSwitchKeepsTheVoiceContinuous()
    {
        var router = Router(AudioSource.Microphone, talkOver: true, autoMute: true);
        router.RequestedSource = AudioSource.Application;
        var o = Run(router, 10);

        // The microphone never fades out: every sample still carries at least the voice minus the incoming music.
        Assert.All(o, v => Assert.True(v >= App + Mic - Tol && v <= Mic + Tol, $"unexpected sample {v}"));
        Assert.Equal(App + Mic, o[^1], Tol);

        router.RequestedSource = AudioSource.Microphone;
        o = Run(router, 10);
        Assert.Equal(Mic, o[^1], Tol);
    }

    [Fact]
    public void TalkOverMusic_TogglingFadesTheMicrophoneSmoothly()
    {
        var router = Router(AudioSource.Application, talkOver: false, autoMute: true);
        var samples = new List<float>();
        router.TalkOverMusic = true;
        samples.AddRange(Run(router, 5));
        router.TalkOverMusic = false;
        samples.AddRange(Run(router, 5));

        float maxJump = 0;
        for (int i = 2; i < samples.Count; i += 2) maxJump = Math.Max(maxJump, Math.Abs(samples[i] - samples[i - 2]));
        Assert.True(maxJump < 0.01f, $"discontinuity {maxJump}");
        Assert.Equal(App, samples[^1], Tol);
    }

    [Fact]
    public void TalkOverMusic_RespectsIndependentVolumes()
    {
        var router = Router(AudioSource.Application, talkOver: true, autoMute: true);
        router.Microphone.TargetGain = 2f;
        router.Application.TargetGain = 0.5f;
        var o = Run(router, 20);
        Assert.Equal(App * 0.5f + Mic * 2f, o[^1], Tol); // -0.25 + 0.5
    }

    [Fact]
    public void WithoutTalkOverMusic_ClassicSwitchStillNeverMixesSources()
    {
        var router = Router(AudioSource.Microphone, talkOver: false, autoMute: true);
        router.RequestedSource = AudioSource.Application;
        var samples = Run(router, 20);
        int firstApp = samples.FindIndex(v => v < 0f);
        Assert.True(firstApp > 0);
        Assert.DoesNotContain(samples.Skip(firstApp), v => v > 0f);
    }

    // ---- limiter, meter, devices --------------------------------------------------------------------

    [Fact]
    public void VoiceMusicAndSoundboardTogetherAreLimited()
    {
        var router = new AudioRouter(Rate, AudioSource.Application) { TalkOverMusic = true, AutoMuteMicrophone = false };
        router.Microphone.TargetGain = 2f;
        router.Application.TargetGain = 2f;
        router.Soundboard.Volume = 2f;
        router.Soundboard.Play("loud", new AudioClip(TestSignals.Sine(Rate, 300, Rate, 0.9f), Rate));
        float max = 0;
        for (int i = 0; i < 60; i++)
        {
            double phase = i * 2 * Math.PI * 200 * Block / Rate;
            router.Microphone.Write(TestSignals.Sine(Rate, 200, Block, 0.9f, phase), Block, Rate);
            router.Application.Write(TestSignals.Sine(Rate, 200, Block, 0.9f, phase), Block, Rate);
            var o = new float[Block * 2];
            router.Render(o, Block);
            max = Math.Max(max, TestSignals.Peak(o));
        }

        Assert.True(max <= 0.8915f, $"peak {max}");
        Assert.True(router.TakeOutputPeak() > 0.8f, "the output meter shows the final, limited mix");
    }

    [Fact]
    public void OutputMeterReflectsTheEffectiveMix()
    {
        var router = Router(AudioSource.Application, talkOver: true, autoMute: true);
        router.TakeOutputPeak();
        Run(router, 3);
        Assert.Equal(Math.Abs(App + Mic), router.TakeOutputPeak(), 0.01f);

        router.Soundboard.Play("x", Sound(2));
        Run(router, 5);
        router.TakeOutputPeak();
        Run(router, 3);
        Assert.Equal(Math.Abs(App + Snd), router.TakeOutputPeak(), 0.01f); // microphone excluded while muted
    }

    [Fact]
    public void MicrophoneDisconnectAndReconnectWhileTalkingOverMusic()
    {
        var router = Router(AudioSource.Application, talkOver: true, autoMute: true);
        router.Microphone.Clear(); // device unplugged: no more data
        for (int i = 0; i < 10; i++)
        {
            var o = Step(router, feedMic: false);
            if (i > 2) Assert.Equal(App, o[^1], Tol); // music continues, silence instead of the voice
        }

        var back = Run(router, 15); // plugged back in
        Assert.Equal(App + Mic, back[^1], Tol);
    }

    [Fact]
    public void StateFlagsReflectTheEffectiveState()
    {
        var router = Router(AudioSource.Application, talkOver: true, autoMute: true);
        Assert.True(router.IsMicrophoneOpen);
        Assert.True(router.IsApplicationOpen);
        Assert.False(router.IsSoundboardActive);

        router.Soundboard.Play("x", Sound(1));
        Step(router);
        Step(router);
        Assert.True(router.IsSoundboardActive);
        Assert.True(router.IsMicrophoneSuppressed);
        Assert.False(router.IsMicrophoneOpen);
        Assert.Equal((0f, 1f), router.GateLevels);
    }

    [Fact]
    public void HeadphoneMonitorStaysSoundboardOnlyWithTalkOverMusic()
    {
        var router = Router(AudioSource.Application, talkOver: true, autoMute: true);
        var monitor = new MonitorMix(Rate);
        monitor.Mixer.Volume = 1f;
        var clip = Sound(1);
        router.Soundboard.Play("x", clip);
        monitor.Mixer.Play("x", clip);
        for (int i = 0; i < 20; i++)
        {
            Step(router);
            var m = new float[Block * 2];
            monitor.Render(m, Block);

            // Only the soundboard is monitored: no microphone (no feedback) and no application audio.
            if (i > 1) Assert.All(m, v => Assert.Equal(Snd, v, Tol));
        }
    }
}
