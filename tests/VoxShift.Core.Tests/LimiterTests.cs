using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class LimiterTests
{
    [Fact]
    public void QuietSignalPassesUnchanged()
    {
        var l = new PeakLimiter(48000);
        var buf = TestSignals.Sine(48000, 1000, 4800, 0.5f);
        var copy = (float[])buf.Clone();
        l.Process(buf, 4800);
        Assert.Equal(copy, buf);
    }

    [Fact]
    public void LoudSignalNeverExceedsCeiling()
    {
        var l = new PeakLimiter(48000);
        var buf = TestSignals.Sine(48000, 1000, 4800, 3f);
        l.Process(buf, 4800);
        Assert.True(TestSignals.Peak(buf) <= l.Ceiling + 1e-6f);
    }

    [Fact]
    public void NaNIsSanitised()
    {
        var l = new PeakLimiter(48000);
        var buf = new[] { float.NaN, float.PositiveInfinity, 0.1f, 0.1f };
        l.Process(buf, 2);
        Assert.All(buf, v => Assert.False(float.IsNaN(v) || float.IsInfinity(v)));
    }
}
