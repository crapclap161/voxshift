using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

/// <summary>
/// Lifecycle and concurrency scenarios of the audio graph that only show up while several threads work on it, or
/// when a device goes away and comes back: the situations a long VRChat session actually runs into.
/// </summary>
public class ReliabilityTests
{
    private const int Rate = 48000;
    private const int Block = 480;

    private static void AssertSane(ReadOnlySpan<float> output)
    {
        foreach (float sample in output)
        {
            Assert.True(float.IsFinite(sample), "output contains NaN or infinity");
            Assert.InRange(sample, -1f, 1f);
        }
    }

    [Fact]
    public void ConcurrentCaptureRenderAndControlThreadsNeverCorruptTheMix()
    {
        // Real threading model: two capture threads write, the render thread pulls, the UI thread switches modes,
        // toggles options, moves sliders and plays / stops sounds, all at the same time. Dedicated threads (as in
        // the application) so the test does not depend on how fast the thread pool grows on a small CI machine.
        const int BlocksToRender = 300;
        var router = new AudioRouter(Rate) { TalkOverMusic = true };
        var clip = new AudioClip(TestSignals.Sine(Rate, 330, Rate / 5, 0.5f), Rate);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        int rendered = 0;
        bool Running() => Volatile.Read(ref rendered) < BlocksToRender && failures.IsEmpty;

        Thread Start(string name, Action body)
        {
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failures.Enqueue(ex); }
            })
            {
                IsBackground = true,
                Name = name,
            };
            thread.Start();
            return thread;
        }

        void Capture(SourceChannel channel, int rate, double freq)
        {
            var block = TestSignals.Sine(rate, freq, rate / 100, 0.6f);
            while (Running())
            {
                channel.Write(block, rate / 100, rate);
                Thread.Yield();
            }
        }

        var threads = new[]
        {
            Start("mic capture", () => Capture(router.Microphone, 44100, 220)),
            Start("app capture", () => Capture(router.Application, 48000, 880)),
            Start("render", () =>
            {
                var output = new float[Block * 2];
                while (Running())
                {
                    router.Render(output, Block);
                    AssertSane(output);
                    Interlocked.Increment(ref rendered);
                }
            }),
            Start("control", () =>
            {
                for (int i = 0; Running(); i++)
                {
                    router.RequestedSource = (i & 1) == 0 ? AudioSource.Application : AudioSource.Microphone;
                    router.TalkOverMusic = (i & 2) == 0;
                    router.AutoMuteMicrophone = (i & 4) == 0;
                    router.Microphone.TargetGain = (i % 5) * 0.5f;
                    router.Soundboard.Volume = (i % 3) * 0.75f;
                    router.Soundboard.Play("s" + (i % 7), clip);
                    if (i % 3 == 0) router.Soundboard.Stop("s" + (i % 7));
                    if (i % 11 == 0) router.Soundboard.StopAll();
                    _ = router.Soundboard.IsPlaying("s1");
                    _ = router.TakeOutputPeak();
                    _ = router.GateLevels;
                    Thread.Yield();
                }
            }),
        };

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"The {thread.Name} thread did not finish: deadlock?");
        }

        Assert.Empty(failures);
        Assert.Equal(BlocksToRender, Volatile.Read(ref rendered));
        Assert.InRange(router.Soundboard.ActiveCount, 0, SoundboardMixer.HardVoiceLimit);
    }

    [Fact]
    public void OutputOutageNeverReplaysStaleAudioWhenTheDeviceReturns()
    {
        // The virtual cable disappears: nothing renders, but the microphone keeps capturing into its buffer.
        var router = new AudioRouter(Rate);
        var output = new float[Block * 2];
        var loud = TestSignals.Constant(Block, 0.5f, 0.5f);
        for (int i = 0; i < 20; i++)
        {
            router.Microphone.Write(loud, Block, Rate);
            router.Render(output, Block);
        }

        for (int i = 0; i < 500; i++) router.Microphone.Write(loud, Block, Rate); // 5 s without any rendering

        // The device is back: the engine restarts the output, the microphone is silent now.
        router.SetOutputRate(Rate);
        var silence = new float[Block * 2];
        int audibleBlocks = 0;
        for (int i = 0; i < 100; i++)
        {
            router.Microphone.Write(silence, Block, Rate);
            router.Render(output, Block);
            if (TestSignals.Peak(output) > 0.01f) audibleBlocks++;
        }

        // At most the jitter-buffer limit of old audio may be heard, never the seconds captured during the outage.
        Assert.True(audibleBlocks <= 25, $"{audibleBlocks} blocks of stale audio were replayed");
        Assert.True(router.Microphone.BufferedFrames <= Rate * router.Microphone.MaxLatencyMs / 1000);
    }

    [Fact]
    public void OutputRateChangeDropsRunningSoundsAndReleasesTheMicrophone()
    {
        // Device switch while a sound plays: its clip was prepared for the old rate and cannot continue.
        var router = new AudioRouter(Rate);
        var output = new float[Block * 2];
        var voice = TestSignals.Constant(Block, 0.25f, 0.25f);
        router.Soundboard.Play("horn", new AudioClip(new float[Rate * 2 * 2], Rate));
        for (int i = 0; i < 10; i++)
        {
            router.Microphone.Write(voice, Block, Rate);
            router.Render(output, Block);
        }

        Assert.True(router.IsMicrophoneSuppressed);

        router.SetOutputRate(44100);
        Assert.Equal(0, router.Soundboard.ActiveCount);
        var voice441 = TestSignals.Constant(441, 0.25f, 0.25f);
        for (int i = 0; i < 60; i++)
        {
            router.Microphone.Write(voice441, 441, 44100);
            router.Render(output, 441);
        }

        // Auto-mute follows the real mixer state, so the dropped sound cannot leave the microphone muted.
        Assert.False(router.IsMicrophoneSuppressed);
        Assert.InRange(output[(441 * 2) - 1], 0.24f, 0.26f); // last sample of the 441-frame block
    }

    [Fact]
    public void RapidConfigurationChangesSettleOnTheLastRequestedState()
    {
        var router = new AudioRouter(Rate);
        var output = new float[Block * 2];
        var mic = TestSignals.Constant(Block, 0.25f, 0.25f);
        var app = TestSignals.Constant(Block, -0.5f, -0.5f);
        void Step()
        {
            router.Microphone.Write(mic, Block, Rate);
            router.Application.Write(app, Block, Rate);
            router.Render(output, Block);
        }

        // Faster than the fades: every block gets a different configuration.
        for (int i = 0; i < 200; i++)
        {
            router.RequestedSource = (i % 2 == 0) ? AudioSource.Application : AudioSource.Microphone;
            router.TalkOverMusic = i % 3 == 0;
            Step();
            AssertSane(output);
        }

        router.RequestedSource = AudioSource.Application;
        router.TalkOverMusic = false;
        for (int i = 0; i < 30; i++) Step();

        Assert.InRange(output[^1], -0.51f, -0.49f); // application only, no trace of the microphone
        Assert.Equal((0f, 1f), router.GateLevels);
    }

    [Fact]
    public void PlayingMoreThanTheVoiceLimitFadesOutTheOldestSoundsFirst()
    {
        var mixer = new SoundboardMixer(Rate);
        var clip = new AudioClip(new float[Rate * 2], Rate);
        for (int i = 0; i < SoundboardMixer.MaxVoices + 3; i++) mixer.Play("s" + i, clip);

        Assert.False(mixer.IsPlaying("s0"));
        Assert.False(mixer.IsPlaying("s1"));
        Assert.False(mixer.IsPlaying("s2"));
        Assert.True(mixer.IsPlaying("s3"));
        Assert.True(mixer.IsPlaying("s" + (SoundboardMixer.MaxVoices + 2)));

        var buffer = new float[Block * 2];
        for (int i = 0; i < 5; i++) mixer.MixInto(buffer, Block); // 50 ms > fade-out
        Assert.Equal(SoundboardMixer.MaxVoices, mixer.ActiveCount);
    }

    [Fact]
    public void VoicesStayBoundedWhenNothingRenders()
    {
        // Regression: fading voices are only released by rendering. Without it (device gone, or clicks arriving
        // faster than audio blocks) the voice list used to grow with every click.
        var mixer = new SoundboardMixer(Rate);
        var clip = new AudioClip(new float[Rate * 2], Rate);
        for (int i = 0; i < 1000; i++) mixer.Play("s" + i, clip);

        Assert.Equal(SoundboardMixer.HardVoiceLimit, mixer.ActiveCount);
        Assert.True(mixer.IsPlaying("s999")); // the newest sounds are the ones that survive
        Assert.False(mixer.IsPlaying("s0"));

        var buffer = new float[Block * 2];
        for (int i = 0; i < 5; i++) mixer.MixInto(buffer, Block);
        Assert.Equal(SoundboardMixer.MaxVoices, mixer.ActiveCount); // back to the normal limit once audio runs again
    }

    [Fact]
    public void StoppingASoundThatIsNotPlayingIsHarmless()
    {
        var router = new AudioRouter(Rate);
        var output = new float[Block * 2];
        router.Soundboard.Stop("never-played");
        router.Soundboard.StopAll();
        router.Render(output, Block);
        Assert.Equal(0, router.Soundboard.ActiveCount);
        Assert.False(router.IsSoundboardActive);
    }
}
