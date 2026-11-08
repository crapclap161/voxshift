using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class SourceChannelTests
{
    [Fact]
    public void PrebuffersThenPlays()
    {
        var ch = new SourceChannel("t", 48000);
        var outBuf = new float[480 * 2];

        Assert.Equal(0, ch.Read(outBuf, 480)); // nothing written yet → silence
        Assert.All(outBuf, v => Assert.Equal(0f, v));

        ch.Write(TestSignals.Constant(480 * 6, 0.5f, 0.5f), 480 * 6, 48000);
        Assert.Equal(480, ch.Read(outBuf, 480));
        Assert.Equal(0.5f, outBuf[2 * 479], 2);
    }

    [Fact]
    public void GainIsAppliedAndSmoothed()
    {
        var ch = new SourceChannel("t", 48000) { TargetGain = 1f };
        var outBuf = new float[480 * 2];
        for (int i = 0; i < 20; i++)
        {
            ch.Write(TestSignals.Constant(480, 0.5f, 0.5f), 480, 48000);
            if (i > 5) ch.Read(outBuf, 480);
        }

        ch.TargetGain = 2f;
        ch.Write(TestSignals.Constant(480, 0.5f, 0.5f), 480, 48000);
        ch.Read(outBuf, 480);
        Assert.True(outBuf[0] < 0.6f, "gain must ramp, not jump");

        for (int i = 0; i < 10; i++)
        {
            ch.Write(TestSignals.Constant(480, 0.5f, 0.5f), 480, 48000);
            ch.Read(outBuf, 480);
        }

        Assert.Equal(1.0f, outBuf[2 * 479], 2);
    }

    [Fact]
    public void PeakMeterReportsPostGainLevelAndResets()
    {
        var ch = new SourceChannel("t", 48000) { TargetGain = 1.5f };
        ch.Write(TestSignals.Constant(100, 0.4f, -0.2f), 100, 48000);
        Assert.Equal(0.6f, ch.TakePeak(), 4);
        Assert.Equal(0f, ch.TakePeak());
    }

    [Theory]
    [InlineData(1.0005)] // producer clock 500 ppm fast
    [InlineData(0.9995)] // producer clock 500 ppm slow
    public void DriftCompensationKeepsLatencyBoundedWithoutUnderruns(double producerSpeed)
    {
        const int outRate = 48000;
        int inRate = 44100;
        var ch = new SourceChannel("t", outRate);
        var outBuf = new float[480 * 2];
        double inPerBlock = 441 * producerSpeed;
        double carry = 0;
        double phase = 0;
        int warmup = 50;

        // Simulate 10 minutes of 10 ms blocks.
        for (int block = 0; block < 60_000; block++)
        {
            carry += inPerBlock;
            int n = (int)carry;
            carry -= n;
            var input = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                float v = (float)(0.3 * Math.Sin(phase));
                phase += 2 * Math.PI * 300 / inRate;
                input[2 * i] = v;
                input[2 * i + 1] = v;
            }

            ch.Write(input, n, inRate);
            ch.Read(outBuf, 480);
            if (block == warmup) Assert.Equal(0, ch.Underruns);
        }

        double bufferedMs = ch.BufferedFrames * 1000.0 / inRate;
        Assert.InRange(bufferedMs, 10, 80);
        Assert.True(ch.Underruns <= 1, $"underruns: {ch.Underruns}");
    }

    [Fact]
    public void IdleTrimsBufferToTarget()
    {
        var ch = new SourceChannel("t", 48000);
        ch.Write(TestSignals.Constant(48000, 0.1f, 0.1f), 48000, 48000); // one second
        ch.Idle();
        Assert.InRange(ch.BufferedFrames, 0, (int)(48000 * ch.TargetLatencyMs / 1000) + 1);
    }

    [Fact]
    public void ExcessBacklogIsDroppedToBoundLatency()
    {
        var ch = new SourceChannel("t", 48000);
        ch.Write(TestSignals.Constant(48000, 0.1f, 0.1f), 48000, 48000);
        var outBuf = new float[480 * 2];
        ch.Read(outBuf, 480);
        Assert.True(ch.BufferedFrames < 48000 * ch.MaxLatencyMs / 1000);
    }
}
