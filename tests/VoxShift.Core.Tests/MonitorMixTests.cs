using VoxShift.Core.Audio;
using VoxShift.Core.Config;

namespace VoxShift.Core.Tests;

public sealed class MonitorMixTests : IDisposable
{
    private const int Block = 480;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voxshift-mon-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static AudioClip Dc(float v, int rate, int frames) => new(TestSignals.Constant(frames, v, v), rate);

    private static float[] Render(IStereoRenderSource source, int blocks)
    {
        float[] last = [];
        for (int i = 0; i < blocks; i++)
        {
            last = new float[Block * 2];
            source.Render(last, Block);
        }

        return last;
    }

    [Fact]
    public void MonitorPlaysSoundboardInYourHeadphones()
    {
        var monitor = new MonitorMix(44100);
        monitor.Mixer.Volume = 1f;
        monitor.Mixer.Play("horn", Dc(0.4f, 44100, 44100));
        Assert.Equal(0.4f, Render(monitor, 5)[^1], 0.001f);
    }

    [Fact]
    public void MonitorIsSilentWithoutSoundsAndOverwritesStaleBufferContent()
    {
        var monitor = new MonitorMix(48000);
        var buffer = Enumerable.Repeat(0.7f, Block * 2).ToArray(); // garbage from a previous render
        monitor.Render(buffer, Block);
        Assert.All(buffer, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void MonitorAndVrchatMixesAreIndependent()
    {
        // Same sound, two devices with different rates and volumes (headphones 44.1 kHz, cable 48 kHz).
        var router = new AudioRouter(48000, AudioSource.Microphone);
        var monitor = new MonitorMix(44100);
        router.Soundboard.Volume = 1.5f;
        monitor.Mixer.Volume = 0.5f;
        router.Soundboard.Play("a", Dc(0.2f, 48000, 48000));
        monitor.Mixer.Play("a", Dc(0.2f, 44100, 44100));

        Assert.Equal(0.3f, Render(router, 5)[^1], 0.001f);
        Assert.Equal(0.1f, Render(monitor, 5)[^1], 0.001f);

        // Stopping in one mix does not affect the other (the player always stops both explicitly).
        monitor.Mixer.StopAll();
        Render(monitor, 3);
        Assert.Equal(0, monitor.Mixer.ActiveCount);
        Assert.True(router.Soundboard.IsPlaying("a"));
    }

    [Fact]
    public void MonitorNeverContainsMicrophoneOrApplicationAudio()
    {
        var router = new AudioRouter(48000, AudioSource.Microphone);
        var monitor = new MonitorMix(48000);
        for (int i = 0; i < 10; i++)
        {
            router.Microphone.Write(TestSignals.Constant(Block, 0.5f, 0.5f), Block, 48000);
            router.Application.Write(TestSignals.Constant(Block, -0.5f, -0.5f), Block, 48000);
            router.Render(new float[Block * 2], Block);
            Assert.All(Render(monitor, 1), v => Assert.Equal(0f, v));
        }
    }

    [Fact]
    public void MonitorIsLimited()
    {
        var monitor = new MonitorMix(48000);
        monitor.Mixer.Volume = 2f;
        monitor.Mixer.Play("loud", new AudioClip(TestSignals.Sine(48000, 200, 48000, 0.95f), 48000));
        float max = 0;
        for (int i = 0; i < 30; i++) max = Math.Max(max, TestSignals.Peak(Render(monitor, 1)));
        Assert.True(max <= 0.8915f, $"peak {max}");
    }

    [Fact]
    public void RateChangeDropsVoices()
    {
        var monitor = new MonitorMix(48000);
        monitor.Mixer.Play("a", Dc(0.2f, 48000, 48000));
        monitor.SetOutputRate(44100);
        Assert.Equal(0, monitor.Mixer.ActiveCount);
        Assert.Equal(44100, monitor.OutputRate);
    }

    [Theory]
    [InlineData("{headphones}", "Headphones (Arctis 7)", "{cable}", true)]
    [InlineData("{cable}", "CABLE Input (VB-Audio Virtual Cable)", "{cable}", false)] // default device = the cable
    [InlineData("{other}", "CABLE Input (VB-Audio Virtual Cable)", null, false)]     // a cable, even if not ours
    [InlineData("", "Speakers", null, false)]
    [InlineData("{speakers}", "Speakers (Realtek(R) Audio)", null, true)]
    public void NeverMonitorsIntoTheVirtualCable(string id, string name, string? cableId, bool expected) =>
        Assert.Equal(expected, MonitorMix.IsSafeMonitorDevice(id, name, cableId));

    [Fact]
    public void MonitorSettingsRoundTripAndDefaultOnForUpgrades()
    {
        Directory.CreateDirectory(_dir);
        // settings.json written by VoxShift 0.0.3 (schema 2, no monitor keys)
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "SchemaVersion": 2, "SoundboardVolumePercent": 90 }""");
        var store = new SettingsStore(_dir);
        var loaded = store.Load();
        Assert.True(loaded.SoundboardMonitorEnabled);
        Assert.Equal(70, loaded.SoundboardMonitorVolumePercent);
        Assert.Equal(90, loaded.SoundboardVolumePercent);

        loaded.SoundboardMonitorEnabled = false;
        loaded.SoundboardMonitorVolumePercent = 350;
        store.Save(loaded);
        var again = new SettingsStore(_dir).Load();
        Assert.False(again.SoundboardMonitorEnabled);
        Assert.Equal(200, again.SoundboardMonitorVolumePercent);
        Assert.Equal(AppSettings.CurrentSchemaVersion, again.SchemaVersion);
    }
}
