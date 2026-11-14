namespace VoxShift.Core.Apps;

/// <summary>
/// Decides when a running application capture should be re-attached. A capture that delivers silence is normal
/// (nothing playing), so silence alone never triggers a restart: it must be combined with evidence that audio is now
/// rendered by a process the capture did not include (see <see cref="ProcessTree.FindUnseenAudioProcesses"/>).
/// A cool-down prevents restart loops.
/// </summary>
public sealed class SilentCaptureWatchdog
{
    private readonly TimeSpan _silenceThreshold;
    private readonly TimeSpan _cooldown;
    private DateTime _lastRestartUtc = DateTime.MinValue;

    public SilentCaptureWatchdog(TimeSpan silenceThreshold, TimeSpan cooldown)
    {
        _silenceThreshold = silenceThreshold;
        _cooldown = cooldown;
    }

    /// <summary>Cheap pre-check: has the capture delivered no signal for at least the threshold?</summary>
    public bool IsSilent(DateTime nowUtc, DateTime lastSignalUtc) => nowUtc - lastSignalUtc >= _silenceThreshold;

    /// <summary>True (once per cool-down) when the capture is silent although new audio processes are playing.</summary>
    public bool ShouldRestart(DateTime nowUtc, DateTime lastSignalUtc, bool newAudioProcessesInTree)
    {
        if (!newAudioProcessesInTree || !IsSilent(nowUtc, lastSignalUtc)) return false;
        if (nowUtc - _lastRestartUtc < _cooldown) return false;
        _lastRestartUtc = nowUtc;
        return true;
    }
}
