using NAudio.MediaFoundation;
using NAudio.Wave;
using VoxShift.Core.Audio;

namespace VoxShift.Audio.Soundboard;

/// <summary>
/// Decodes a local audio file into an in-memory <see cref="AudioClip"/> (stereo float, native rate).
/// WAV is read by VoxShift's own decoder; everything else (and unusual WAV encodings) goes through Windows
/// Media Foundation, which provides MP3, AAC/M4A, WMA and FLAC on Windows 10/11. Files are opened read-only
/// with full sharing, so the original is never locked against other programs or modified.
/// </summary>
public static class AudioFileDecoder
{
    /// <summary>Longest sound kept in memory; longer files are truncated (use application mode for music).</summary>
    public const int MaxSeconds = 300;

    private const long MaxWavBytes = 512L * 1024 * 1024;
    private static readonly object MfStartupLock = new();
    private static bool _mfStarted;

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="AudioCaptureException">The file could not be decoded.</exception>
    public static AudioClip Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Sound file not found.", path);

        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            var clip = TryDecodeWav(path);
            if (clip is not null) return clip;
        }

        return DecodeWithMediaFoundation(path);
    }

    private static AudioClip? TryDecodeWav(string path)
    {
        using var stream = OpenShared(path);
        if (stream.Length > MaxWavBytes) return null;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var clip = WavDecoder.TryDecode(bytes);
        if (clip is null) return null;
        int max = MaxSeconds * clip.SampleRate;
        return clip.Frames <= max ? clip : new AudioClip(clip.Samples.AsSpan(0, max * 2).ToArray(), clip.SampleRate);
    }

    /// <summary>Decodes through Windows Media Foundation (also used directly by the smoke test).</summary>
    public static AudioClip DecodeWithMediaFoundation(string path)
    {
        EnsureMediaFoundation();
        try
        {
            using var stream = OpenShared(path);
            using var reader = new StreamMediaFoundationReader(stream, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
            var provider = reader.ToSampleProvider();
            int channels = provider.WaveFormat.Channels;
            int rate = provider.WaveFormat.SampleRate;
            if (channels < 1 || rate <= 0) throw new AudioCaptureException("The file reports an invalid audio format.");

            long maxFrames = (long)MaxSeconds * rate;
            var stereo = new List<float>(capacity: (int)Math.Min(maxFrames * 2, rate * 2 * 30));
            var buffer = new float[rate / 10 * channels];
            long frames = 0;
            int read;
            while (frames < maxFrames && (read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                int n = read / channels;
                for (int f = 0; f < n && frames < maxFrames; f++, frames++)
                {
                    float l = buffer[f * channels];
                    float r = channels > 1 ? buffer[f * channels + 1] : l;
                    stereo.Add(l);
                    stereo.Add(r);
                }
            }

            if (frames == 0) throw new AudioCaptureException("The file contains no audio.");
            return new AudioClip(stereo.ToArray(), rate);
        }
        catch (Exception ex) when (ex is not AudioCaptureException and not FileNotFoundException)
        {
            throw new AudioCaptureException(
                $"Windows could not decode \"{Path.GetFileName(path)}\" ({ex.Message}). Supported: WAV, MP3, FLAC, M4A/AAC, WMA.",
                ex.HResult, ex);
        }
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    private static void EnsureMediaFoundation()
    {
        lock (MfStartupLock)
        {
            if (_mfStarted) return;
            MediaFoundationApi.Startup();
            _mfStarted = true;
        }
    }
}
