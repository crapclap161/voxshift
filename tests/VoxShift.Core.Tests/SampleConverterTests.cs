using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class SampleConverterTests
{
    [Fact]
    public void Pcm16Mono_IsDuplicatedToStereo()
    {
        byte[] data = new byte[4];
        BitConverter.GetBytes((short)16384).CopyTo(data, 0);
        BitConverter.GetBytes((short)-32768).CopyTo(data, 2);
        var dest = new float[4];

        SampleConverter.ToStereoFloat(data, new StreamFormat(SampleFormat.Pcm16, 1, 48000), 2, dest);

        Assert.Equal(new[] { 0.5f, 0.5f, -1f, -1f }, dest);
    }

    [Fact]
    public void Pcm24_SignExtendsNegativeValues()
    {
        // -4194304 = 0xC00000 → -0.5
        byte[] data = { 0x00, 0x00, 0xC0, 0x00, 0x00, 0x40 };
        var dest = new float[2];

        SampleConverter.ToStereoFloat(data, new StreamFormat(SampleFormat.Pcm24, 2, 48000), 1, dest);

        Assert.Equal(-0.5f, dest[0], 5);
        Assert.Equal(0.5f, dest[1], 5);
    }

    [Fact]
    public void Float32Stereo_IsCopied()
    {
        float[] src = { 0.1f, -0.2f, 0.3f, -0.4f };
        byte[] data = new byte[src.Length * 4];
        Buffer.BlockCopy(src, 0, data, 0, data.Length);
        var dest = new float[4];

        SampleConverter.ToStereoFloat(data, new StreamFormat(SampleFormat.Float32, 2, 44100), 2, dest);

        Assert.Equal(src, dest);
    }

    [Fact]
    public void MultiChannel_MixesCentreIntoFrontPair()
    {
        float[] src = { 0.2f, 0.4f, 0.5f, 0.9f }; // L R C LFE
        byte[] data = new byte[16];
        Buffer.BlockCopy(src, 0, data, 0, 16);
        var dest = new float[2];

        SampleConverter.ToStereoFloat(data, new StreamFormat(SampleFormat.Float32, 4, 48000), 1, dest);

        Assert.Equal(0.2f + 0.5f * 0.7071f, dest[0], 4);
        Assert.Equal(0.4f + 0.5f * 0.7071f, dest[1], 4);
    }

    [Fact]
    public void StereoToChannels_MapsToMonoAndSurround()
    {
        float[] stereo = { 0.2f, 0.6f };
        var mono = new float[1];
        SampleConverter.StereoToChannels(stereo, 1, mono, 1);
        Assert.Equal(0.4f, mono[0], 5);

        var six = new float[6];
        SampleConverter.StereoToChannels(stereo, 1, six, 6);
        Assert.Equal(new[] { 0.2f, 0.6f, 0f, 0f, 0f, 0f }, six);
    }

    [Fact]
    public void TooSmallSource_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SampleConverter.ToStereoFloat(new byte[3], new StreamFormat(SampleFormat.Float32, 1, 48000), 1, new float[2]));
    }
}
