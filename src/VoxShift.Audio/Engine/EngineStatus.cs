using VoxShift.Core.Audio;

namespace VoxShift.Audio.Engine;

public enum ComponentState
{
    /// <summary>Nothing selected / not needed.</summary>
    Idle,

    /// <summary>Waiting for something external (device plugged in, application started).</summary>
    Waiting,

    Running,

    Error,
}

/// <summary>Immutable snapshot of the engine for the UI.</summary>
public sealed record EngineStatus
{
    public ComponentState OutputState { get; init; } = ComponentState.Waiting;

    public string OutputMessage { get; init; } = "Starting…";

    public string? OutputDeviceName { get; init; }

    public string? OutputDeviceId { get; init; }

    /// <summary>The recording device VRChat must use (e.g. "CABLE Output (VB-Audio Virtual Cable)").</summary>
    public string? VrchatInputHint { get; init; }

    public bool VirtualCableInstalled { get; init; }

    public ComponentState MicrophoneState { get; init; } = ComponentState.Waiting;

    public string MicrophoneMessage { get; init; } = "Starting…";

    public string? MicrophoneDeviceName { get; init; }

    public ComponentState ApplicationState { get; init; } = ComponentState.Idle;

    public string ApplicationMessage { get; init; } = "No application selected.";

    public string? ApplicationName { get; init; }

    public int? ApplicationProcessId { get; init; }

    public bool ProcessLoopbackSupported { get; init; } = true;

    /// <summary>Local soundboard monitoring (your headphones).</summary>
    public ComponentState MonitorState { get; init; } = ComponentState.Idle;

    public string MonitorMessage { get; init; } = string.Empty;

    public string? MonitorDeviceName { get; init; }

    public AudioSource Mode { get; init; }

    /// <summary>Non-fatal configuration problem worth showing (e.g. feedback-loop risk).</summary>
    public string? Warning { get; init; }

    /// <summary>True when the selected source is actually flowing into the virtual cable.</summary>
    public bool IsTransmitting => OutputState == ComponentState.Running &&
        (Mode == AudioSource.Microphone ? MicrophoneState == ComponentState.Running : ApplicationState == ComponentState.Running);
}
