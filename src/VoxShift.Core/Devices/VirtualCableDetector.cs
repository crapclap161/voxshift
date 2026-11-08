namespace VoxShift.Core.Devices;

/// <summary>A device endpoint as presented to the UI (no COM objects).</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault = false)
{
    public override string ToString() => Name;
}

/// <summary>
/// Recognises virtual audio cable endpoints by name. The router writes to the cable's playback side
/// ("CABLE Input"); VRChat records from its capture side ("CABLE Output").
/// </summary>
public static class VirtualCableDetector
{
    // Ordered by preference: the free VB-CABLE first, then its A/B variants, then other common drivers.
    private static readonly (string RenderFragment, string CaptureHint)[] Known =
    {
        ("CABLE Input", "CABLE Output (VB-Audio Virtual Cable)"),
        ("CABLE-A Input", "CABLE-A Output (VB-Audio Cable A)"),
        ("CABLE-B Input", "CABLE-B Output (VB-Audio Cable B)"),
        ("CABLE-C Input", "CABLE-C Output (VB-Audio Cable C)"),
        ("CABLE-D Input", "CABLE-D Output (VB-Audio Cable D)"),
        ("Virtual Audio Cable", "Line 1 (Virtual Audio Cable)"),
        ("Line 1 (Virtual Audio Cable)", "Line 1 (Virtual Audio Cable)"),
    };

    /// <summary>Returns true if a playback endpoint name looks like a virtual cable input.</summary>
    public static bool IsVirtualCableRender(string name) => Known.Any(k => name.Contains(k.RenderFragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns true if a recording endpoint is the receiving side of a virtual cable. Such devices must
    /// never be used as "microphone" – that would feed the router's own output back into itself.
    /// </summary>
    public static bool IsVirtualCableCapture(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) && name.Contains("Output", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) && !name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the preferred virtual cable among playback endpoints, or null if none is installed.</summary>
    public static AudioDeviceInfo? FindPreferred(IEnumerable<AudioDeviceInfo> renderDevices)
    {
        var list = renderDevices.ToList();
        foreach (var (fragment, _) in Known)
        {
            var match = list.FirstOrDefault(d => d.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>The name of the recording device the user must pick in VRChat for a given cable input.</summary>
    public static string GetVrchatInputHint(string renderName)
    {
        foreach (var (fragment, hint) in Known)
        {
            if (renderName.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return hint;
        }

        return "the recording side of \"" + renderName + "\"";
    }
}
