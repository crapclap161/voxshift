using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VoxShift.Core.Devices;
using VoxShift.Core.Logging;

namespace VoxShift.Audio.Devices;

/// <summary>
/// Device discovery and hot-plug monitoring (IMMNotificationClient). Only plain <see cref="AudioDeviceInfo"/>
/// records leave this class, so the UI never touches COM objects. Must be used from an MTA thread
/// (the engine thread).
/// </summary>
public sealed class DeviceService : IDisposable
{
    private readonly ILog _log;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notifications;
    private bool _registered;

    public DeviceService(ILog log)
    {
        _log = log;
        _enumerator = new MMDeviceEnumerator();
        _notifications = new NotificationClient(this);
        try
        {
            _registered = _enumerator.RegisterEndpointNotificationCallback(_notifications) == 0;
        }
        catch (Exception ex)
        {
            _log.Warn("Could not register for audio device notifications; device changes will be picked up by polling.", ex);
        }
    }

    /// <summary>Raised (on an arbitrary thread) when devices are added/removed/changed or defaults change.</summary>
    public event Action? Changed;

    public MMDeviceEnumerator Enumerator => _enumerator;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        string? defaultId = GetDefaultId(flow);
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                    }
                    catch (Exception ex)
                    {
                        _log.Debug($"Skipping device that could not be queried: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Enumerating {flow} devices failed.", ex);
        }

        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string? GetDefaultId(DataFlow flow)
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(flow, Role.Console)) return null;
            using var device = _enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Returns the active device with the given id, or null. Caller owns (disposes) the result.</summary>
    public MMDevice? TryGetActive(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var device = _enumerator.GetDevice(id);
            if (device.State == DeviceState.Active) return device;
            device.Dispose();
        }
        catch (Exception)
        {
            // Unknown id (device uninstalled) – treated as not present.
        }

        return null;
    }

    /// <summary>Default console endpoint for the flow, or null. Caller owns the result.</summary>
    public MMDevice? TryGetDefault(DataFlow flow)
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(flow, Role.Console) ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Console) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); }
            catch (Exception) { }
            _registered = false;
        }

        _enumerator.Dispose();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.Warn("Device change handler failed.", ex); }
    }

    /// <summary>Called by Windows on its own threads – only signal, never do COM work here.</summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly DeviceService _owner;

        public NotificationClient(DeviceService owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.RaiseChanged();

        public void OnDeviceAdded(string pwstrDeviceId) => _owner.RaiseChanged();

        public void OnDeviceRemoved(string deviceId) => _owner.RaiseChanged();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _owner.RaiseChanged();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Ignored: fires very frequently (e.g. volume/meter related properties) and never affects routing.
        }
    }
}
