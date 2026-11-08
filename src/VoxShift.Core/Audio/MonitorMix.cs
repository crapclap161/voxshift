namespace VoxShift.Core.Audio;

/// <summary>
/// Local monitoring of the soundboard: what you hear in your own headphones. It has its own
/// <see cref="SoundboardMixer"/> because the headphones and the virtual cable run on independent clocks (and
/// possibly different sample rates), plus its own volume and limiter. It contains soundboard audio only. Your
/// microphone is never echoed back to you, and the captured application already plays on your headphones.
/// </summary>
public sealed class MonitorMix : IStereoRenderSource
{
    private readonly PeakLimiter _limiter;

    public MonitorMix(int outputRate = 48000)
    {
        Mixer = new SoundboardMixer(outputRate);
        _limiter = new PeakLimiter(outputRate);
    }

    public SoundboardMixer Mixer { get; }

    public int OutputRate => Mixer.OutputRate;

    public void SetOutputRate(int outputRate)
    {
        Mixer.SetOutputRate(outputRate);
        _limiter.SetSampleRate(outputRate);
        _limiter.Reset();
    }

    public void Render(Span<float> destination, int frames)
    {
        var block = destination[..(frames * 2)];
        block.Clear();
        Mixer.MixInto(block, frames);
        _limiter.Process(block, frames);
    }

    /// <summary>
    /// Whether local monitoring may use the given playback device. Never monitor into the virtual cable itself,
    /// because VRChat would then receive every sound twice.
    /// </summary>
    public static bool IsSafeMonitorDevice(string deviceId, string deviceName, string? cableOutputId) =>
        !string.IsNullOrEmpty(deviceId)
        && !string.Equals(deviceId, cableOutputId, StringComparison.OrdinalIgnoreCase)
        && !Devices.VirtualCableDetector.IsVirtualCableRender(deviceName);
}
