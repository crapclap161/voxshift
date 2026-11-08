namespace VoxShift.Core.Audio;

/// <summary>Raw sample encodings that WASAPI may deliver in shared mode.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Float32 is the established audio term for 32-bit IEEE float samples.")]
public enum SampleFormat
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>Minimal description of an incoming interleaved stream.</summary>
public readonly record struct StreamFormat(SampleFormat Format, int Channels, int SampleRate)
{
    public int BytesPerSample => Format switch
    {
        SampleFormat.Pcm16 => 2,
        SampleFormat.Pcm24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, {Format}";
}
