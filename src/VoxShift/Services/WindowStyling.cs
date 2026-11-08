using System;
using System.Runtime.InteropServices;

namespace VoxShift.Services;

/// <summary>Windows 11 DWM tweaks: dark title bar, matching caption colour, rounded corners.</summary>
public static class WindowStyling
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDarkChrome(IntPtr hwnd, byte r, byte g, byte b)
    {
        // Every call is best effort: older Windows builds simply ignore unknown attributes.
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int colorRef = r | (g << 8) | (b << 16);
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
        int border = 0x3A2F2A; // #2A2F3A as COLORREF (0x00BBGGRR)
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }
}
