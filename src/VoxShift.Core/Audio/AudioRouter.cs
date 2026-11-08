namespace VoxShift.Core.Audio;

/// <summary>
/// The heart of the switcher. Exactly one <see cref="SourceChannel"/> is transmitted at any time, optionally
/// with the <see cref="SoundboardMixer"/> layered on top.
/// A mode change fades the current source out completely (~8 ms), then fades the new one in, so the two
/// sources are <b>never mixed</b> – not even during the transition – and there is no click or pop.
/// The inactive source is continuously trimmed and discarded on the render thread.
/// </summary>
public sealed class AudioRouter
{
    private const double FadeMs = 8;

    private readonly object _renderSync = new();
    private float[] _scratch = new float[4096 * 2];
    private readonly PeakLimiter _limiter;
    private volatile int _requested;
    private int _active;
    private float _fade = 1f;
    private float _fadeStep;
    private int _outputPeakBits;

    public AudioRouter(int outputRate, AudioSource initialSource = AudioSource.Microphone)
    {
        OutputRate = outputRate;
        Microphone = new SourceChannel("Microphone", outputRate);
        Application = new SourceChannel("Application", outputRate);
        Soundboard = new SoundboardMixer(outputRate);
        _limiter = new PeakLimiter(outputRate);
        _requested = (int)initialSource;
        _active = (int)initialSource;
        _fadeStep = (float)(1000.0 / (FadeMs * outputRate));
    }

    public SourceChannel Microphone { get; }

    public SourceChannel Application { get; }

    /// <summary>
    /// Soundboard layer. It is mixed on top of whichever source is transmitted, after the source fade and before the
    /// limiter and output meter, so it is unaffected by mode switches and can never cause microphone or
    /// application leakage.
    /// </summary>
    public SoundboardMixer Soundboard { get; }

    public int OutputRate { get; private set; }

    /// <summary>The source the user selected. Takes effect within one fade period.</summary>
    public AudioSource RequestedSource
    {
        get => (AudioSource)_requested;
        set => _requested = (int)value;
    }

    /// <summary>The source whose samples are currently being written to the output.</summary>
    public AudioSource ActiveSource => (AudioSource)Volatile.Read(ref _active);

    /// <summary>Whether the limiter is currently reducing gain (shown as a "limiting" hint in the UI).</summary>
    public bool IsLimiting => _limiter.CurrentGain < 0.98f;

    public SourceChannel GetChannel(AudioSource source) => source == AudioSource.Microphone ? Microphone : Application;

    /// <summary>Must be called (before rendering starts) whenever the output device's sample rate changes.</summary>
    public void SetOutputRate(int outputRate)
    {
        lock (_renderSync)
        {
            OutputRate = outputRate;
            Microphone.SetOutputRate(outputRate);
            Application.SetOutputRate(outputRate);
            Soundboard.SetOutputRate(outputRate);
            _limiter.SetSampleRate(outputRate);
            _limiter.Reset();
            _fadeStep = (float)(1000.0 / (FadeMs * outputRate));
        }
    }

    /// <summary>
    /// Produces <paramref name="frames"/> frames of interleaved stereo float output. Called from the
    /// output device's render thread.
    /// </summary>
    public void Render(Span<float> destination, int frames)
    {
        lock (_renderSync)
        {
            RenderLocked(destination.Slice(0, frames * 2), frames);
        }
    }

    private void RenderLocked(Span<float> output, int frames)
    {
        int requested = _requested;
        var active = GetChannel((AudioSource)_active);
        var inactive = GetChannel((AudioSource)(1 - _active));

        inactive.Idle();

        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
        Span<float> buffer = _scratch.AsSpan(0, frames * 2);
        active.Read(buffer, frames);

        float fade = _fade;
        float step = _fadeStep;
        bool switched = false;
        for (int f = 0; f < frames; f++)
        {
            if (switched)
            {
                // Remainder of the block after the hand-over is silent; the new source fades in next block.
                output[2 * f] = 0f;
                output[2 * f + 1] = 0f;
                continue;
            }

            if (requested != _active)
            {
                fade -= step;
                if (fade <= 0f)
                {
                    fade = 0f;
                    switched = true;
                }
            }
            else if (fade < 1f)
            {
                fade = Math.Min(1f, fade + step);
            }

            output[2 * f] = buffer[2 * f] * fade;
            output[2 * f + 1] = buffer[2 * f + 1] * fade;
        }

        if (switched)
        {
            // The old source is now fully silent: hand over. Its buffered audio is discarded so that it
            // can never leak into the new mode.
            active.Clear();
            _active = requested;
            GetChannel((AudioSource)requested).Activate();
        }

        _fade = fade;

        // Soundboard layer: independent of the active source and of the switching fade.
        Soundboard.MixInto(output, frames);

        _limiter.Process(output, frames);

        float peak = 0f;
        for (int i = 0; i < frames * 2; i++)
        {
            float a = Math.Abs(output[i]);
            if (a > peak) peak = a;
        }

        int bits = BitConverter.SingleToInt32Bits(peak);
        while (true)
        {
            int cur = Volatile.Read(ref _outputPeakBits);
            if (BitConverter.Int32BitsToSingle(cur) >= peak) break;
            if (Interlocked.CompareExchange(ref _outputPeakBits, bits, cur) == cur) break;
        }
    }

    /// <summary>Highest output peak since the previous call (resets).</summary>
    public float TakeOutputPeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _outputPeakBits, 0));
}
