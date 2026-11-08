namespace VoxShift.ViewModels;

/// <summary>An entry of the microphone / output dropdowns. Empty <see cref="Id"/> = automatic choice.</summary>
public sealed record DeviceItem(string Id, string Name, string? Detail = null)
{
    public override string ToString() => Name;
}

/// <summary>An entry of the application dropdown.</summary>
public sealed record AppItem(string ExeName, string DisplayName, int? ProcessId, string Detail, bool IsPlaying, bool IsRunning)
{
    public string PlayingGlyph => IsPlaying ? "" : string.Empty; // Play icon

    public override string ToString() => DisplayName;
}
