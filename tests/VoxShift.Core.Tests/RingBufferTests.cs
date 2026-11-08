using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class RingBufferTests
{
    [Fact]
    public void WriteRead_PreservesOrderAcrossWrap()
    {
        var rb = new StereoRingBuffer(4);
        rb.Write(new float[] { 1, 1, 2, 2, 3, 3 }, 3);
        var tmp = new float[4];
        Assert.Equal(2, rb.Read(tmp, 2));
        Assert.Equal(new float[] { 1, 1, 2, 2 }, tmp);

        rb.Write(new float[] { 4, 4, 5, 5, 6, 6 }, 3); // wraps
        var all = new float[8];
        Assert.Equal(4, rb.Read(all, 4));
        Assert.Equal(new float[] { 3, 3, 4, 4, 5, 5, 6, 6 }, all);
        Assert.Equal(0, rb.Count);
    }

    [Fact]
    public void Overflow_DropsOldestFrames()
    {
        var rb = new StereoRingBuffer(3);
        rb.Write(new float[] { 1, 1, 2, 2, 3, 3 }, 3);
        rb.Write(new float[] { 4, 4 }, 1);

        var tmp = new float[6];
        Assert.Equal(3, rb.Read(tmp, 3));
        Assert.Equal(new float[] { 2, 2, 3, 3, 4, 4 }, tmp);
        Assert.Equal(1, rb.OverflowFrames);
    }

    [Fact]
    public void WriteLargerThanCapacity_KeepsNewest()
    {
        var rb = new StereoRingBuffer(2);
        rb.Write(new float[] { 1, 1, 2, 2, 3, 3, 4, 4 }, 4);
        var tmp = new float[4];
        rb.Read(tmp, 2);
        Assert.Equal(new float[] { 3, 3, 4, 4 }, tmp);
    }
}
