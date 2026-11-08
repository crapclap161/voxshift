using System.Buffers.Binary;

namespace VoxShift.Core.Audio;

/// <summary>
/// Minimal RIFF/WAVE reader for PCM (8/16/24/32-bit) and IEEE float (32/64-bit) data, including
/// WAVE_FORMAT_EXTENSIBLE. Produces stereo float at the file's own sample rate. Other encodings (ADPCM, µ-law …)
/// are reported as unsupported so the caller can fall back to the Windows decoders.
/// </summary>
public static class WavDecoder
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Decodes a WAV file image. Returns null if the data is not a WAV this decoder understands.</summary>
    /// <param name="maxFrames">Truncate after this many frames (protects memory for very long files).</param>
    public static AudioClip? TryDecode(ReadOnlySpan<byte> data, int maxFrames = int.MaxValue)
    {
        if (data.Length < 12 || !Is(data, 0, "RIFF") || !Is(data, 8, "WAVE")) return null;

        int offset = 12;
        ushort format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        bool haveFormat = false;

        while (offset + 8 <= data.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            int bodyStart = offset + 8;
            long bodyEnd = Math.Min((long)bodyStart + size, data.Length);

            if (Is(data, offset, "fmt ") && size >= 16 && bodyStart + 16 <= data.Length)
            {
                var fmt = data.Slice(bodyStart, (int)Math.Min(size, (uint)(data.Length - bodyStart)));
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == FormatExtensible && fmt.Length >= 26)
                {
                    // The first two bytes of the SubFormat GUID carry the actual format tag.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
                }

                haveFormat = true;
            }
            else if (Is(data, offset, "data") && haveFormat)
            {
                return Decode(data[bodyStart..(int)bodyEnd], format, channels, sampleRate, bits, maxFrames);
            }

            // Chunks are word aligned.
            long next = (long)bodyStart + size + (size & 1);
            if (next > int.MaxValue) break;
            offset = (int)next;
        }

        return null;
    }

    private static AudioClip? Decode(ReadOnlySpan<byte> pcm, ushort format, ushort channels, int sampleRate, ushort bits, int maxFrames)
    {
        if (channels == 0 || sampleRate <= 0 || sampleRate > 768_000) return null;
        bool isFloat = format == FormatFloat;
        if (!isFloat && format != FormatPcm) return null;
        if (isFloat && bits != 32 && bits != 64) return null;
        if (!isFloat && bits != 8 && bits != 16 && bits != 24 && bits != 32) return null;

        int bytesPerSample = bits / 8;
        int blockAlign = bytesPerSample * channels;
        int frames = Math.Min(pcm.Length / blockAlign, maxFrames);
        var stereo = new float[frames * 2];

        for (int f = 0; f < frames; f++)
        {
            int baseOffset = f * blockAlign;
            float l = Read(pcm, baseOffset, bits, isFloat);
            float r = channels > 1 ? Read(pcm, baseOffset + bytesPerSample, bits, isFloat) : l;
            stereo[2 * f] = l;
            stereo[2 * f + 1] = r;
        }

        return new AudioClip(stereo, sampleRate);
    }

    private static float Read(ReadOnlySpan<byte> src, int offset, int bits, bool isFloat)
    {
        if (isFloat)
        {
            return bits == 32
                ? BinaryPrimitives.ReadSingleLittleEndian(src.Slice(offset, 4))
                : (float)BinaryPrimitives.ReadDoubleLittleEndian(src.Slice(offset, 8));
        }

        switch (bits)
        {
            case 8:
                return (src[offset] - 128) / 128f;
            case 16:
                return BinaryPrimitives.ReadInt16LittleEndian(src.Slice(offset, 2)) / 32768f;
            case 24:
                int v = src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16);
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                return v / 8388608f;
            default:
                return BinaryPrimitives.ReadInt32LittleEndian(src.Slice(offset, 4)) / 2147483648f;
        }
    }

    private static bool Is(ReadOnlySpan<byte> data, int offset, string tag) =>
        offset + 4 <= data.Length && data[offset] == tag[0] && data[offset + 1] == tag[1] && data[offset + 2] == tag[2] && data[offset + 3] == tag[3];
}
