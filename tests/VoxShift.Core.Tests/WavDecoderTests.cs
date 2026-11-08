using System.Text;
using VoxShift.Core.Audio;

namespace VoxShift.Core.Tests;

public class WavDecoderTests
{
    private static byte[] Wav(ushort format, ushort channels, int rate, ushort bits, byte[] data, bool extensible = false, bool extraChunk = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        if (extraChunk)
        {
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            w.Write(3);
            w.Write(new byte[] { 1, 2, 3, 0 }); // odd size + pad byte
        }

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(extensible ? 40 : 16);
        w.Write(extensible ? (ushort)0xFFFE : format);
        w.Write(channels);
        w.Write(rate);
        w.Write(rate * channels * bits / 8);
        w.Write((ushort)(channels * bits / 8));
        w.Write(bits);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write(bits);
            w.Write(3u);
            w.Write(format); // SubFormat GUID starts with the format tag
            w.Write(new byte[14]);
        }

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public void DecodesPcm16Mono()
    {
        var data = new byte[4];
        BitConverter.GetBytes((short)16384).CopyTo(data, 0);
        BitConverter.GetBytes((short)-32768).CopyTo(data, 2);
        var clip = WavDecoder.TryDecode(Wav(1, 1, 44100, 16, data));
        Assert.NotNull(clip);
        Assert.Equal(44100, clip!.SampleRate);
        Assert.Equal(new[] { 0.5f, 0.5f, -1f, -1f }, clip.Samples);
    }

    [Fact]
    public void DecodesFloatStereoExtensibleAfterUnknownChunk()
    {
        float[] src = { 0.1f, -0.2f, 0.3f, -0.4f };
        var data = new byte[16];
        Buffer.BlockCopy(src, 0, data, 0, 16);
        var clip = WavDecoder.TryDecode(Wav(3, 2, 48000, 32, data, extensible: true, extraChunk: true));
        Assert.Equal(src, clip!.Samples);
    }

    [Fact]
    public void Decodes24BitAnd8Bit()
    {
        var c24 = WavDecoder.TryDecode(Wav(1, 1, 48000, 24, [0x00, 0x00, 0xC0]));
        Assert.Equal(-0.5f, c24!.Samples[0], 5);
        var c8 = WavDecoder.TryDecode(Wav(1, 1, 8000, 8, [192]));
        Assert.Equal(0.5f, c8!.Samples[0], 5);
    }

    [Fact]
    public void TruncatesToMaxFrames()
    {
        var clip = WavDecoder.TryDecode(Wav(1, 1, 48000, 16, new byte[2000]), maxFrames: 100);
        Assert.Equal(100, clip!.Frames);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E' })]
    public void ReturnsNullForGarbage(byte[] data) => Assert.Null(WavDecoder.TryDecode(data));

    [Fact]
    public void ReturnsNullForCompressedWav()
    {
        Assert.Null(WavDecoder.TryDecode(Wav(2 /* MS ADPCM */, 1, 44100, 4, new byte[16])));
    }

    [Fact]
    public void ResamplesClipToOutputRateKeepingPitchAndLength()
    {
        var clip = new AudioClip(TestSignals.Sine(44100, 440, 44100, 0.5f), 44100);
        var resampled = clip.ResampleTo(48000);
        Assert.Equal(48000, resampled.SampleRate);
        Assert.InRange(resampled.Frames, 47990, 48010);
        Assert.InRange(TestSignals.EstimateFrequency(resampled.Samples.AsSpan(4000, 80000), 48000), 438.5, 441.5);
        Assert.Same(resampled, resampled.ResampleTo(48000));
    }
}
