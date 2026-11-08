namespace VoxShift.Audio;

/// <summary>A capture/render failure with a user-presentable message.</summary>
public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException(string message, int hresult = 0, Exception? inner = null)
        : base(message, inner)
    {
        if (hresult != 0) HResult = hresult;
    }
}
