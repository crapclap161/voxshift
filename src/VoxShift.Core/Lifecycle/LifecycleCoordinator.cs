namespace VoxShift.Core.Lifecycle;

/// <summary>Where the application is in its close / tray / shutdown life.</summary>
public enum AppLifecycleState
{
    /// <summary>Normal operation.</summary>
    Running,

    /// <summary>The user asked to close the main window and the decision (dialog or saved preference) is being handled.</summary>
    CloseConfirmationPending,

    /// <summary>The interface is being hidden into the system tray.</summary>
    MinimizingToTray,

    /// <summary>The application is shutting down; no window may be shown, restored or re-created any more.</summary>
    ShuttingDown,

    /// <summary>Shutdown finished.</summary>
    Exited,
}

/// <summary>What the user chose for a close request.</summary>
public enum CloseChoice
{
    Cancel,
    MinimizeToTray,
    Quit,
}

/// <summary>
/// The single source of truth for the close / minimize / shutdown workflow. Every operation that may be triggered from
/// several places (the X button, rapid repeated clicks, the tray menu, secondary windows, queued dispatcher callbacks)
/// must first win a transition here, so each runs exactly once and can never re-enter itself.
/// </summary>
public sealed class LifecycleCoordinator
{
    private readonly object _gate = new();
    private AppLifecycleState _state = AppLifecycleState.Running;

    public AppLifecycleState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>True once shutdown has begun: windows must not be shown or restored and queued UI work must be dropped.</summary>
    public bool IsShuttingDown => State >= AppLifecycleState.ShuttingDown;

    /// <summary>Starts handling a close request. False when one is already being handled or the app is leaving.</summary>
    public bool TryBeginCloseRequest() => Transition(AppLifecycleState.Running, AppLifecycleState.CloseConfirmationPending);

    /// <summary>The close request ended without leaving (cancelled, or finished minimizing).</summary>
    public bool EndCloseRequest() => Transition(AppLifecycleState.CloseConfirmationPending, AppLifecycleState.Running);

    /// <summary>Starts hiding into the tray (from normal operation or from a pending close request).</summary>
    public bool TryBeginMinimizeToTray()
    {
        lock (_gate)
        {
            if (_state is not (AppLifecycleState.Running or AppLifecycleState.CloseConfirmationPending)) return false;
            _state = AppLifecycleState.MinimizingToTray;
            return true;
        }
    }

    public bool EndMinimizeToTray() => Transition(AppLifecycleState.MinimizingToTray, AppLifecycleState.Running);

    /// <summary>Wins the right to run the one and only shutdown sequence. Every later call returns false.</summary>
    public bool TryBeginShutdown()
    {
        lock (_gate)
        {
            if (_state >= AppLifecycleState.ShuttingDown) return false;
            _state = AppLifecycleState.ShuttingDown;
            return true;
        }
    }

    public void MarkExited()
    {
        lock (_gate) _state = AppLifecycleState.Exited;
    }

    private bool Transition(AppLifecycleState from, AppLifecycleState to)
    {
        lock (_gate)
        {
            if (_state != from) return false;
            _state = to;
            return true;
        }
    }
}
