using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VoxShift.Core.Layout;

namespace VoxShift.Services;

/// <summary>Win32/DWM helpers for docking windows precisely (physical pixels, per-monitor DPI aware).</summary>
internal static class NativeWindowMethods
{
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCAPTION = 2;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelRect ToPixelRect() => PixelRect.FromEdges(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>The visible frame of a window (without the invisible resize borders of Windows 10/11).</summary>
    public static PixelRect GetVisibleFrame(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0 && frame.Right > frame.Left)
        {
            return frame.ToPixelRect();
        }

        return GetWindowRect(hwnd, out var rect) ? rect.ToPixelRect() : default;
    }

    public static PixelRect GetWindowBounds(IntPtr hwnd) => GetWindowRect(hwnd, out var rect) ? rect.ToPixelRect() : default;

    /// <summary>Work area (screen without taskbar) of the monitor nearest to the window.</summary>
    public static PixelRect GetWorkArea(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.rcWork.ToPixelRect() : default;
    }

    /// <summary>Work areas of all connected monitors.</summary>
    public static IReadOnlyList<PixelRect> GetAllWorkAreas()
    {
        var result = new List<PixelRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info)) result.Add(info.rcWork.ToPixelRect());
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Moves and resizes a window without activating it or changing its z-order.</summary>
    public static void MoveWindow(IntPtr hwnd, PixelRect rect) =>
        SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Moves a window by an offset without resizing or activating it.</summary>
    public static void OffsetWindow(IntPtr hwnd, int dx, int dy)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        SetWindowPos(hwnd, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Starts a native "drag by caption" of <paramref name="hwnd"/> (used to move the main window from the dock).</summary>
    public static void BeginCaptionDrag(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    /// <summary>Keeps a window out of Alt+Tab (tool window), used for the dock which belongs to the main window.</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW));
    }
}
