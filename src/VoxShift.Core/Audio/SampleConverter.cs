using System.Buffers.Binary;

namespace VoxShift.Core.Audio;

/// <summary>
/// Converts raw interleaved capture buffers of any supported encoding / channel count into the
/// internal processing format: interleaved stereo 32-bit float.
/// </summary>
public static class SampleConverter
{
    /// <summary>
    /// Converts <paramref name="frames"/> frames from <paramref name="source"/> into stereo float.
    /// Mono input is duplicated to both channels; for more than two channels the front left/right
    /// pair is used (with centre mixed in when present) so a 5.1 microphone array still works.
    /// </summary>
    /// <returns>Number of frames written (always <paramref name="frames"/>).</returns>
    public static int ToStereoFloat(ReadOnlySpan<byte> source, StreamFormat format, int frames, Span<float> destination)
    {
        if (format.Channels < 1) throw new ArgumentOutOfRangeException(nameof(format), "Channel count must be positive.");
        int needed = frames * format.BlockAlign;
        if (source.Length < needed) throw new ArgumentException("Source buffer is smaller than the frame count implies.", nameof(source));
        if (destination.Length < frames * 2) throw new ArgumentException("Destination buffer is too small.", nameof(destination));

        int ch = format.Channels;
        int bps = format.BytesPerSample;
        int block = format.BlockAlign;

        for (int f = 0; f < frames; f++)
        {
            int baseOffset = f * block;
            float l = Read(source, baseOffset, format.Format);
            float r;
            if (ch == 1)
            {
                r = l;
            }
            else
            {
                r = Read(source, baseOffset + bps, format.Format);
                if (ch >= 3)
                {
                    // Channel 3 is front centre in every standard WAVE_FORMAT_EXTENSIBLE layout with >= 3 channels.
                    float c = Read(source, baseOffset + 2 * bps, format.Format) * 0.7071f;
                    l += c;
                    r += c;
                }
            }

            destination[2 * f] = l;
            destination[2 * f + 1] = r;
        }

        return frames;
    }

    private static float Read(ReadOnlySpan<byte> src, int offset, SampleFormat format)
    {
        switch (format)
        {
            case SampleFormat.Float32:
                return BinaryPrimitives.ReadSingleLittleEndian(src.Slice(offset, 4));
            case SampleFormat.Pcm16:
                return BinaryPrimitives.ReadInt16LittleEndian(src.Slice(offset, 2)) / 32768f;
            case SampleFormat.Pcm24:
                {
                    int v = src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    return v / 8388608f;
                }
            case SampleFormat.Pcm32:
                return BinaryPrimitives.ReadInt32LittleEndian(src.Slice(offset, 4)) / 2147483648f;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// Writes interleaved stereo float into an interleaved float buffer with <paramref name="outChannels"/>
    /// channels (mono = average, 2 = copy, more = L/R into the first two channels, silence elsewhere).
    /// </summary>
    public static void StereoToChannels(ReadOnlySpan<float> stereo, int frames, Span<float> destination, int outChannels)
    {
        if (outChannels == 2)
        {
            stereo.Slice(0, frames * 2).CopyTo(destination);
            return;
        }

        for (int f = 0; f < frames; f++)
        {
            float l = stereo[2 * f];
            float r = stereo[2 * f + 1];
            int o = f * outChannels;
            if (outChannels == 1)
            {
                destination[o] = 0.5f * (l + r);
                continue;
            }

            destination[o] = l;
            destination[o + 1] = r;
            for (int c = 2; c < outChannels; c++) destination[o + c] = 0f;
        }
    }
}
