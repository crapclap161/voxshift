using System;
using System.Threading;

namespace VoxShift.Services;

/// <summary>
/// Ensures only one instance runs (two instances would both write into the virtual cable and VRChat
/// would hear duplicated audio). A second launch just brings the first window to the front.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\VoxShift.SingleInstance";
    private const string EventName = @"Local\VoxShift.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
    }

    /// <summary>Returns the instance guard, or null if another instance is already running (it gets activated).</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out bool createdNew);
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!createdNew)
        {
            evt.Set();
            evt.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, evt);
    }

    /// <summary>Invokes <paramref name="callback"/> (on a thread-pool thread) whenever another instance starts.</summary>
    public void ListenForActivation(Action callback)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _activateEvent.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
