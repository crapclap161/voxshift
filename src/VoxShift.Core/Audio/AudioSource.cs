namespace VoxShift.Core.Audio;

/// <summary>The source that is transmitted into the virtual cable (and therefore into VRChat).</summary>
public enum AudioSource
{
    /// <summary>The physical microphone selected by the user.</summary>
    Microphone = 0,

    /// <summary>The audio of the selected application (process loopback capture).</summary>
    Application = 1,
}
