namespace VoxShift.Core.Audio;

/// <summary>Everything that decides which sources VRChat hears.</summary>
/// <param name="Mode">The source the user selected (microphone mode or application audio mode).</param>
/// <param name="TalkOverMusic">In application mode, also transmit the microphone ("Talk over music").</param>
/// <param name="AutoMuteMicrophone">Suppress the microphone while soundboard sounds are actually playing.</param>
/// <param name="SoundboardPlaying">At least one soundboard voice is playing in the VRChat mix (real mixer state).</param>
public readonly record struct MixInputs(AudioSource Mode, bool TalkOverMusic, bool AutoMuteMicrophone, bool SoundboardPlaying);

/// <summary>Which router inputs are transmitted. The soundboard layer is always added on top when it plays.</summary>
/// <param name="Microphone">The microphone contribution is open.</param>
/// <param name="Application">The application contribution is open.</param>
/// <param name="MicrophoneSuppressed">
/// The microphone would be transmitted, but is temporarily muted because a soundboard sound is playing.
/// </param>
public readonly record struct MixDecision(bool Microphone, bool Application, bool MicrophoneSuppressed);

/// <summary>
/// The single, deterministic rule set behind VoxShift's audio routing (see the behaviour matrix in the README):
/// <list type="bullet">
/// <item>Microphone mode transmits the microphone only. Application audio is never sent.</item>
/// <item>Application mode transmits the application. The microphone is added only when Talk over music is on.</item>
/// <item>Auto-mute takes precedence: while a soundboard sound plays it suppresses the microphone, also over music.</item>
/// </list>
/// The decision is recalculated from the current inputs on every audio block, so it can never restore a stale state.
/// </summary>
public static class MixPolicy
{
    public static MixDecision Decide(MixInputs inputs)
    {
        bool application = inputs.Mode == AudioSource.Application;
        bool microphoneWanted = inputs.Mode == AudioSource.Microphone || inputs.TalkOverMusic;
        bool suppressed = microphoneWanted && inputs.AutoMuteMicrophone && inputs.SoundboardPlaying;
        return new MixDecision(microphoneWanted && !suppressed, application, suppressed);
    }
}
