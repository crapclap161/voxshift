using NAudio.Wave;
using VoxShift.Core.Audio;

namespace VoxShift.Audio.Capture;

internal static class WaveFormatMapper
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Maps an NAudio/WASAPI format to the converter's description, or throws for exotic formats.</summary>
    public static StreamFormat ToStreamFormat(WaveFormat format)
    {
        bool isFloat;
        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat:
                isFloat = true;
                break;
            case WaveFormatEncoding.Pcm:
                isFloat = false;
                break;
            case WaveFormatEncoding.Extensible when format is WaveFormatExtensible ext:
                if (ext.SubFormat == SubtypeIeeeFloat) isFloat = true;
                else if (ext.SubFormat == SubtypePcm) isFloat = false;
                else throw new AudioCaptureException($"Unsupported audio sub-format {ext.SubFormat}.");
                break;
            default:
                throw new AudioCaptureException($"Unsupported audio format {format.Encoding}.");
        }

        SampleFormat sf = (isFloat, format.BitsPerSample) switch
        {
            (true, 32) => SampleFormat.Float32,
            (false, 16) => SampleFormat.Pcm16,
            (false, 24) => SampleFormat.Pcm24,
            (false, 32) => SampleFormat.Pcm32, // also covers 24-bit samples in 32-bit containers
            _ => throw new AudioCaptureException($"Unsupported sample size {format.BitsPerSample} bit ({(isFloat ? "float" : "PCM")})."),
        };

        return new StreamFormat(sf, format.Channels, format.SampleRate);
    }
}
