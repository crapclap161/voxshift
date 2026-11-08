using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using VoxShift.Core.Input;

namespace VoxShift.Services;

/// <summary>
/// System-wide hotkey via RegisterHotKey. Works while VRChat (or any other app) has focus.
/// Note: games running elevated may swallow keys before they reach non-elevated apps.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 0xB00F;

    private readonly HwndSource _source;
    private bool _registered;

    public GlobalHotkey(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Registers the gesture, replacing any previous one. Returns an error message or null.</summary>
    public string? Register(HotkeyGesture gesture)
    {
        Unregister();
        if (!gesture.IsValid) return "Invalid hotkey.";
        if (!Enum.TryParse<Key>(gesture.Key, ignoreCase: true, out var key)) return $"Unknown key \"{gesture.Key}\".";

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return $"Key \"{gesture.Key}\" cannot be used as a hotkey.";

        if (!RegisterHotKey(_source.Handle, HotkeyId, (uint)gesture.Modifiers | MOD_NOREPEAT, (uint)vk))
        {
            int err = Marshal.GetLastWin32Error();
            return err == 1409 // ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{gesture} is already used by another program. Choose a different hotkey."
                : $"Could not register hotkey {gesture} (error {err}).";
        }

        _registered = true;
        return null;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }
}
