namespace VoxShift.Core.Audio;

/// <summary>Playback progress of one active soundboard voice (for UI feedback).</summary>
public readonly record struct VoiceProgress(string SoundId, double Progress);

/// <summary>
/// Mixes any number of simultaneously playing soundboard clips into the router output. It is an additional layer
/// on top of whichever source (microphone or application) is transmitted, so it never changes the
/// "one source at a time" rule. Clips are pre-decoded and resampled to the output rate before they get here,
/// so the render thread only adds samples: no disk I/O, decoding or allocation on the audio thread.
/// </summary>
public sealed class SoundboardMixer
{
    /// <summary>Hard limit of simultaneous voices; the oldest voice is faded out when exceeded.</summary>
    public const int MaxVoices = 32;

    private const double FadeInMs = 3;
    private const double FadeOutMs = 12;

    private readonly object _sync = new();
    private readonly List<Voice> _voices = new(MaxVoices + 1);
    private volatile float _targetGain = 0.8f;
    private float _gain = 0.8f;
    private float _gainCoefficient;
    private float _fadeInStep;
    private float _fadeOutStep;
    private int _peakBits;

    public SoundboardMixer(int outputRate)
    {
        SetOutputRate(outputRate);
        _gain = _targetGain;
    }

    public int OutputRate { get; private set; }

    /// <summary>Linear soundboard volume (0..2), independent of microphone and application volume.</summary>
    public float Volume
    {
        get => _targetGain;
        set
        {
            float v = Math.Clamp(value, 0f, 4f);
            lock (_sync)
            {
                _targetGain = v;
                if (_voices.Count == 0) _gain = v; // nothing audible: apply instantly, ramp only while playing
            }
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_sync) return _voices.Count;
        }
    }

    /// <summary>
    /// Starts a clip. Several clips (even the same sound twice) can play at once. The clip must already be at
    /// <see cref="OutputRate"/>.
    /// </summary>
    public void Play(string soundId, AudioClip clip)
    {
        ArgumentNullException.ThrowIfNull(soundId);
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.SampleRate != OutputRate)
        {
            throw new ArgumentException($"Clip is {clip.SampleRate} Hz but the output runs at {OutputRate} Hz.", nameof(clip));
        }

        if (clip.Frames == 0) return;
        lock (_sync)
        {
            if (_voices.Count >= MaxVoices)
            {
                var oldest = _voices.Where(v => !v.Stopping).OrderBy(v => v.Started).FirstOrDefault();
                if (oldest is not null) oldest.Stopping = true;
            }

            _voices.Add(new Voice(soundId, clip, Environment.TickCount64));
        }
    }

    /// <summary>Fades out every voice of the given sound.</summary>
    public void Stop(string soundId)
    {
        lock (_sync)
        {
            foreach (var v in _voices)
            {
                if (v.SoundId == soundId) v.Stopping = true;
            }
        }
    }

    /// <summary>Fades out all voices (≈12 ms, no click).</summary>
    public void StopAll()
    {
        lock (_sync)
        {
            foreach (var v in _voices) v.Stopping = true;
        }
    }

    public bool IsPlaying(string soundId)
    {
        lock (_sync) return _voices.Any(v => v.SoundId == soundId && !v.Stopping);
    }

    /// <summary>Fills <paramref name="target"/> with the progress of every non-stopping voice.</summary>
    public void CopyProgress(ICollection<VoiceProgress> target)
    {
        target.Clear();
        lock (_sync)
        {
            foreach (var v in _voices)
            {
                if (!v.Stopping) target.Add(new VoiceProgress(v.SoundId, (double)v.Position / v.Clip.Frames));
            }
        }
    }

    /// <summary>Called when the output device (and therefore the rate) changes. Running voices are dropped.</summary>
    public void SetOutputRate(int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        lock (_sync)
        {
            OutputRate = outputRate;
            _voices.Clear();
            _gainCoefficient = (float)(1.0 - Math.Exp(-1.0 / (0.010 * outputRate)));
            _fadeInStep = (float)(1000.0 / (FadeInMs * outputRate));
            _fadeOutStep = (float)(1000.0 / (FadeOutMs * outputRate));
        }
    }

    /// <summary>Removes every voice immediately (used on shutdown).</summary>
    public void Clear()
    {
        lock (_sync) _voices.Clear();
    }

    /// <summary>Highest soundboard peak (post-volume) since the previous call.</summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    /// <summary>Adds all active voices into <paramref name="stereo"/> (render thread). Never allocates.</summary>
    public void MixInto(Span<float> stereo, int frames)
    {
        float peak = 0f;
        lock (_sync)
        {
            if (_voices.Count == 0)
            {
                _gain = _targetGain;
                return;
            }

            float target = _targetGain;
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                var v = _voices[i];
                float[] samples = v.Clip.Samples;
                int clipFrames = v.Clip.Frames;
                float g = _gain;
                float env = v.Envelope;
                int pos = v.Position;

                for (int f = 0; f < frames && pos < clipFrames; f++, pos++)
                {
                    g += (target - g) * _gainCoefficient;
                    if (v.Stopping)
                    {
                        env -= _fadeOutStep;
                        if (env <= 0f)
                        {
                            env = 0f;
                            pos = clipFrames; // finished
                            break;
                        }
                    }
                    else if (env < 1f)
                    {
                        env = Math.Min(1f, env + _fadeInStep);
                    }

                    float k = g * env;
                    float l = samples[2 * pos] * k;
                    float r = samples[2 * pos + 1] * k;
                    stereo[2 * f] += l;
                    stereo[2 * f + 1] += r;
                    float a = Math.Max(Math.Abs(l), Math.Abs(r));
                    if (a > peak) peak = a;
                }

                v.Position = pos;
                v.Envelope = env;
                if (pos >= clipFrames)
                {
                    // Swap-remove (order is irrelevant for mixing).
                    _voices[i] = _voices[^1];
                    _voices.RemoveAt(_voices.Count - 1);
                }
            }

            // Advance the shared gain smoother by one block.
            for (int f = 0; f < frames; f++) _gain += (target - _gain) * _gainCoefficient;
        }

        int bits = BitConverter.SingleToInt32Bits(peak);
        while (true)
        {
            int cur = Volatile.Read(ref _peakBits);
            if (BitConverter.Int32BitsToSingle(cur) >= peak) break;
            if (Interlocked.CompareExchange(ref _peakBits, bits, cur) == cur) break;
        }
    }

    private sealed class Voice
    {
        public Voice(string soundId, AudioClip clip, long started)
        {
            SoundId = soundId;
            Clip = clip;
            Started = started;
        }

        public string SoundId { get; }

        public AudioClip Clip { get; }

        public long Started { get; }

        public int Position { get; set; }

        public float Envelope { get; set; }

        public bool Stopping { get; set; }
    }
}
