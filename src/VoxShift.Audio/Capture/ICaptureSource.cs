using VoxShift.Core.Audio;

namespace VoxShift.Audio.Capture;

/// <summary>
/// Receives captured audio already converted to interleaved stereo float.
/// Invoked on the capture thread – implementations must be fast and non-blocking.
/// </summary>
public delegate void StereoDataHandler(ReadOnlySpan<float> stereo, int frames, int sampleRate);

/// <summary>Common shape of the microphone and application captures.</summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Format of the stream delivered by Windows (before conversion).</summary>
    StreamFormat? Format { get; }

    bool IsRunning { get; }

    /// <summary>Raised when capture stops on its own; the exception is null for a requested stop.</summary>
    event Action<ICaptureSource, Exception?>? Stopped;

    void Start();
}
