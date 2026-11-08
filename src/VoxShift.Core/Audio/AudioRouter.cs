namespace VoxShift.Core.Audio;

/// <summary>
/// The heart of VoxShift: mixes the microphone, the captured application and the soundboard into what VRChat hears.
/// <para>
/// Every source sits behind its own <i>transmit gate</i> (0 = closed, 1 = open). Once per audio block the gates'
/// targets are recalculated by <see cref="MixPolicy"/> from the selected mode, <see cref="TalkOverMusic"/>,
/// <see cref="AutoMuteMicrophone"/> and the soundboard mixer's <b>real</b> playback state. Gates move with short
/// linear fades, so muting, unmuting and mode switches never click or pop.
/// </para>
/// <para>
/// Gates obey <b>close before open</b>: a gate only starts opening once every gate that has to close is fully closed.
/// With Talk over music off, the microphone and the application are therefore never mixed, not even during a mode
/// switch (identical to v0.0.4). A closed source is continuously trimmed and its buffered audio is discarded, so
/// nothing stale can leak out later. The soundboard layer is added on top, then everything goes through one peak
/// limiter and the output meter.
/// </para>
/// </summary>
public sealed class AudioRouter : IStereoRenderSource
{
    /// <summary>Fade used for mode switches and for muting the microphone when a sound starts.</summary>
    public const double FadeMs = 8;

    /// <summary>Softer fade used when the microphone comes back after soundboard playback.</summary>
    public const double MicrophoneRestoreFadeMs = 30;

    /// <summary>
    /// How long the microphone stays muted after the last soundboard voice ended. Bridges the gap between quickly
    /// repeated clicks so the microphone does not flicker in between.
    /// </summary>
    public const double AutoMuteReleaseHoldMs = 100;

    private readonly object _renderSync = new();
    private readonly PeakLimiter _limiter;
    private readonly Gate _micGate;
    private readonly Gate _appGate;
    private float[] _micScratch = new float[4096 * 2];
    private float[] _appScratch = new float[4096 * 2];
    private volatile int _requested;
    private volatile bool _talkOverMusic;
    private volatile bool _autoMuteMicrophone = true;
    private float _fadeStep;
    private float _restoreStep;
    private int _holdFrames;
    private int _holdRemaining;
    private int _outputPeakBits;
    private volatile int _state; // StateFlags of the last rendered block (read by the UI)

    public AudioRouter(int outputRate, AudioSource initialSource = AudioSource.Microphone)
    {
        OutputRate = outputRate;
        Microphone = new SourceChannel("Microphone", outputRate);
        Application = new SourceChannel("Application", outputRate);
        Soundboard = new SoundboardMixer(outputRate);
        _limiter = new PeakLimiter(outputRate);
        _requested = (int)initialSource;
        _micGate = new Gate(Microphone, initialSource == AudioSource.Microphone);
        _appGate = new Gate(Application, initialSource == AudioSource.Application);
        UpdateRates(outputRate);
        _state = (int)(initialSource == AudioSource.Microphone ? StateFlags.MicrophoneOpen : StateFlags.ApplicationOpen);
    }

    [Flags]
    private enum StateFlags
    {
        None = 0,
        MicrophoneOpen = 1,
        ApplicationOpen = 2,
        MicrophoneSuppressed = 4,
        SoundboardActive = 8,
    }

    public SourceChannel Microphone { get; }

    public SourceChannel Application { get; }

    /// <summary>
    /// Soundboard layer. It is mixed on top of the transmitted sources, after their gates and before the limiter and
    /// output meter, so mode switches never interrupt it.
    /// </summary>
    public SoundboardMixer Soundboard { get; }

    public int OutputRate { get; private set; }

    /// <summary>The source the user selected (microphone mode or application mode). Takes effect within one fade.</summary>
    public AudioSource RequestedSource
    {
        get => (AudioSource)_requested;
        set => _requested = (int)value;
    }

    /// <summary>
    /// Talk over music: in application mode, the microphone is transmitted together with the application.
    /// Has no effect in microphone mode (application audio is never sent there).
    /// </summary>
    public bool TalkOverMusic
    {
        get => _talkOverMusic;
        set => _talkOverMusic = value;
    }

    /// <summary>
    /// Automatically suppresses the microphone contribution while soundboard sounds play in the VRChat mix, and
    /// restores it once the last sound has finished. Only VoxShift's mix is affected, never the Windows device.
    /// </summary>
    public bool AutoMuteMicrophone
    {
        get => _autoMuteMicrophone;
        set => _autoMuteMicrophone = value;
    }

    /// <summary>The primary source currently written to the output (for diagnostics and tests).</summary>
    public AudioSource ActiveSource
    {
        get
        {
            var flags = (StateFlags)_state;
            bool mic = flags.HasFlag(StateFlags.MicrophoneOpen);
            bool app = flags.HasFlag(StateFlags.ApplicationOpen);
            if (app && !mic) return AudioSource.Application;
            if (mic && !app) return AudioSource.Microphone;
            return RequestedSource;
        }
    }

    /// <summary>The microphone contribution is (at least partly) open in the last rendered block.</summary>
    public bool IsMicrophoneOpen => ((StateFlags)_state).HasFlag(StateFlags.MicrophoneOpen);

    /// <summary>The application contribution is (at least partly) open in the last rendered block.</summary>
    public bool IsApplicationOpen => ((StateFlags)_state).HasFlag(StateFlags.ApplicationOpen);

    /// <summary>The microphone is temporarily muted by soundboard playback (auto-mute).</summary>
    public bool IsMicrophoneSuppressed => ((StateFlags)_state).HasFlag(StateFlags.MicrophoneSuppressed);

    /// <summary>Soundboard sounds are playing in the VRChat mix (including the short release hold).</summary>
    public bool IsSoundboardActive => ((StateFlags)_state).HasFlag(StateFlags.SoundboardActive);

    /// <summary>Whether the limiter is currently reducing gain (shown as a "limiting" hint in the UI).</summary>
    public bool IsLimiting => _limiter.CurrentGain < 0.98f;

    /// <summary>Current gate positions (0..1), for tests and diagnostics.</summary>
    public (float Microphone, float Application) GateLevels
    {
        get
        {
            lock (_renderSync) return (_micGate.Level, _appGate.Level);
        }
    }

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
            UpdateRates(outputRate);
        }
    }

    private void UpdateRates(int outputRate)
    {
        _fadeStep = (float)(1000.0 / (FadeMs * outputRate));
        _restoreStep = (float)(1000.0 / (MicrophoneRestoreFadeMs * outputRate));
        _holdFrames = (int)(AutoMuteReleaseHoldMs * outputRate / 1000.0);
        _holdRemaining = Math.Min(_holdRemaining, _holdFrames);
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
        // 1) Decide what VRChat should hear right now, from live state only (never from remembered events).
        bool soundboardVoices = Soundboard.ActiveCount > 0;
        if (soundboardVoices) _holdRemaining = _holdFrames;
        else if (_holdRemaining > 0) _holdRemaining = Math.Max(0, _holdRemaining - frames);
        bool soundboardActive = soundboardVoices || _holdRemaining > 0;

        var decision = MixPolicy.Decide(new MixInputs((AudioSource)_requested, _talkOverMusic, _autoMuteMicrophone, soundboardActive));

        // A microphone that returns after soundboard playback fades in softly; mode switches stay fast.
        _micGate.SetTarget(decision.Microphone, decision.Microphone && _micGate.WasSuppressed ? _restoreStep : _fadeStep);
        _micGate.WasSuppressed = decision.MicrophoneSuppressed || (_micGate.WasSuppressed && !decision.Microphone);
        _appGate.SetTarget(decision.Application, _fadeStep);

        // 2) Pull audio only from sources that are (or are about to be) audible; trim the others.
        if (_micScratch.Length < frames * 2) _micScratch = new float[frames * 2];
        if (_appScratch.Length < frames * 2) _appScratch = new float[frames * 2];
        Span<float> mic = _micScratch.AsSpan(0, frames * 2);
        Span<float> app = _appScratch.AsSpan(0, frames * 2);
        bool micReads = _micGate.Prepare(mic, frames);
        bool appReads = _appGate.Prepare(app, frames);

        // 3) Gate fades, sample by sample, with "close before open".
        float micLevel = _micGate.Level, appLevel = _appGate.Level;
        bool micTarget = _micGate.Target, appTarget = _appGate.Target;
        float micClose = _fadeStep, appClose = _fadeStep;
        float micOpen = _micGate.OpenStep, appOpen = _appGate.OpenStep;
        for (int f = 0; f < frames; f++)
        {
            if (!micTarget && micLevel > 0f) micLevel = Math.Max(0f, micLevel - micClose);
            if (!appTarget && appLevel > 0f) appLevel = Math.Max(0f, appLevel - appClose);
            bool closing = (!micTarget && micLevel > 0f) || (!appTarget && appLevel > 0f);
            if (!closing)
            {
                if (micTarget && micLevel < 1f) micLevel = Math.Min(1f, micLevel + micOpen);
                if (appTarget && appLevel < 1f) appLevel = Math.Min(1f, appLevel + appOpen);
            }

            float l = 0f, r = 0f;
            if (micReads)
            {
                l += mic[2 * f] * micLevel;
                r += mic[2 * f + 1] * micLevel;
            }

            if (appReads)
            {
                l += app[2 * f] * appLevel;
                r += app[2 * f + 1] * appLevel;
            }

            output[2 * f] = l;
            output[2 * f + 1] = r;
        }

        _micGate.Finish(micLevel);
        _appGate.Finish(appLevel);

        // 4) Soundboard layer: independent of the gates and of mode switches.
        Soundboard.MixInto(output, frames);

        // 5) One limiter for the whole mix (protects against clipping when voice + music + sounds add up).
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

        var state = StateFlags.None;
        if (micLevel > 0f) state |= StateFlags.MicrophoneOpen;
        if (appLevel > 0f) state |= StateFlags.ApplicationOpen;
        if (decision.MicrophoneSuppressed) state |= StateFlags.MicrophoneSuppressed;
        if (soundboardActive) state |= StateFlags.SoundboardActive;
        _state = (int)state;
    }

    /// <summary>Highest output peak since the previous call (resets).</summary>
    public float TakeOutputPeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _outputPeakBits, 0));

    /// <summary>Transmit gate of one source channel (render thread only, under the render lock).</summary>
    private sealed class Gate
    {
        private readonly SourceChannel _channel;
        private bool _engaged;

        public Gate(SourceChannel channel, bool open)
        {
            _channel = channel;
            Level = open ? 1f : 0f;
            Target = open;
            _engaged = open;
        }

        public float Level { get; private set; }

        public bool Target { get; private set; }

        public float OpenStep { get; private set; } = 1f;

        /// <summary>The previous closing was caused by soundboard auto-mute (selects the softer restore fade).</summary>
        public bool WasSuppressed { get; set; }

        public void SetTarget(bool open, float openStep)
        {
            if (open && !Target) OpenStep = openStep;
            Target = open;
        }

        /// <summary>Reads the channel when it is (or becomes) audible, otherwise keeps it trimmed. Returns whether it was read.</summary>
        public bool Prepare(Span<float> buffer, int frames)
        {
            bool needed = Target || Level > 0f;
            if (!needed)
            {
                _channel.Idle();
                return false;
            }

            if (!_engaged)
            {
                _channel.Activate(); // fresh resampler state, newest audio only
                _engaged = true;
            }

            _channel.Read(buffer, frames);
            return true;
        }

        public void Finish(float level)
        {
            Level = level;
            if (_engaged && !Target && level <= 0f)
            {
                // Fully closed: discard everything buffered so it can never leak into a later block.
                _channel.Clear();
                _engaged = false;
            }
        }
    }
}
