namespace VoxShift.Core.Audio;

/// <summary>Something a WASAPI output can pull interleaved stereo float audio from.</summary>
public interface IStereoRenderSource
{
    /// <summary>Called before rendering starts (and whenever the device's sample rate changes).</summary>
    void SetOutputRate(int outputRate);

    /// <summary>Fills <paramref name="frames"/> stereo frames. Called on the device's render thread.</summary>
    void Render(Span<float> destination, int frames);
}
