namespace VoxShift.Core.Layout;

/// <summary>An axis-aligned rectangle in physical screen pixels (Win32 coordinates; may be negative on multi-monitor setups).</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public PixelRect Offset(int dx, int dy) => this with { Left = Left + dx, Top = Top + dy };

    public bool IntersectsWith(PixelRect other) =>
        Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);
}

/// <summary>Where the soundboard dock goes, and how far the main window has to move so that both fit.</summary>
/// <param name="Dock">Dock window rectangle (physical pixels).</param>
/// <param name="MainShiftX">Horizontal offset to apply to the main window (0 = leave it where it is).</param>
/// <param name="Overlaps">True when the screen is too narrow and the dock had to overlap the main window.</param>
public readonly record struct DockLayout(PixelRect Dock, int MainShiftX, bool Overlaps);

/// <summary>
/// Geometry of the soundboard dock: it sits flush against the right edge of the main window's visible frame, with the
/// same top and height. When there is not enough room on the monitor, the main window is moved left (only when the
/// caller allows it, e.g. after a drag ends), and as a last resort the dock is kept on screen by overlapping the main
/// window. Pure function so it can be unit tested without a desktop.
/// </summary>
public static class DockPlacement
{
    /// <param name="mainFrame">Visible frame of the main window (without the invisible resize borders).</param>
    /// <param name="dockWidth">Desired dock width in physical pixels.</param>
    /// <param name="workArea">Work area (screen minus taskbar) of the monitor the main window is on.</param>
    /// <param name="allowMoveMain">Whether the main window may be moved to make room.</param>
    public static DockLayout Compute(PixelRect mainFrame, int dockWidth, PixelRect workArea, bool allowMoveMain)
    {
        dockWidth = Math.Max(1, dockWidth);
        var dock = new PixelRect(mainFrame.Right, mainFrame.Top, dockWidth, Math.Max(1, mainFrame.Height));
        int shift = 0;

        if (allowMoveMain && dock.Right > workArea.Right)
        {
            int overflow = dock.Right - workArea.Right;
            int room = Math.Max(0, mainFrame.Left - workArea.Left);
            shift = -Math.Min(overflow, room);
            dock = dock.Offset(shift, 0);
        }

        bool overlaps = false;
        if (allowMoveMain && dock.Right > workArea.Right)
        {
            // The monitor is narrower than main window + dock: keep the dock fully visible over the main window.
            int width = Math.Min(dockWidth, Math.Max(1, workArea.Width));
            dock = dock with { Left = Math.Max(workArea.Left, workArea.Right - width), Width = width };
            overlaps = true;
        }

        return new DockLayout(dock, shift, overlaps);
    }

    /// <summary>
    /// Whether a saved window position is still usable: at least a grab-able part of its top edge must be on one of
    /// the connected monitors (protects against monitors that were unplugged since the last run).
    /// </summary>
    public static bool IsReachable(PixelRect window, IEnumerable<PixelRect> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var titleStrip = new PixelRect(window.Left + 40, window.Top, Math.Max(1, window.Width - 80), 32);
        return monitors.Any(m => m.IntersectsWith(titleStrip));
    }
}
