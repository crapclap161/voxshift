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
    public async Task ConcurrentCaptureRenderAndControlThreadsNeverCorruptTheMix()
    {
        // Real threading model: two capture threads write, the render thread pulls, the UI thread switches modes,
        // toggles options, moves sliders and plays / stops sounds, all at the same time.
        var router = new AudioRouter(Rate) { TalkOverMusic = true };
        var clip = new AudioClip(TestSignals.Sine(Rate, 330, Rate / 5, 0.5f), Rate);
        using var stop = new CancellationTokenSource();
        var token = stop.Token;
        long rendered = 0;

        Task Writer(SourceChannel channel, int rate, double freq) => Task.Run(() =>
        {
            var block = TestSignals.Sine(rate, freq, rate / 100, 0.6f);
            while (!token.IsCancellationRequested) channel.Write(block, rate / 100, rate);
        });

        var render = Task.Run(() =>
        {
            var output = new float[Block * 2];
            while (!token.IsCancellationRequested)
            {
                router.Render(output, Block);
                AssertSane(output);
                Interlocked.Increment(ref rendered);
            }
        });

        var control = Task.Run(() =>
        {
            for (int i = 0; !token.IsCancellationRequested; i++)
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
            }
        });

        var tasks = new[] { Writer(router.Microphone, 44100, 220), Writer(router.Application, 48000, 880), render, control };
        await Task.Delay(400);
        stop.Cancel();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20)); // rethrows any failure; a hang means a deadlock

        Assert.True(Interlocked.Read(ref rendered) > 0);
        Assert.True(router.Soundboard.ActiveCount <= SoundboardMixer.MaxVoices + 1);
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
