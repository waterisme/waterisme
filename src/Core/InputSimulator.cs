using System;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Core;

public static class InputSimulator
{
    [DllImport("user32.dll")] private static extern bool   SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void   mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void   keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern uint   MapVirtualKey(uint code, uint mapType);

    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    private const uint MOUSEEVENTF_WHEEL      = 0x0800;
    private const uint KEYEVENTF_KEYUP        = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY  = 0x0001;

    public static void MoveMouse(int monitorIndex, float nx, float ny)
    {
        var monitors = MonitorHelper.GetAll();
        if (monitorIndex >= monitors.Count) return;
        var b = monitors[monitorIndex].Bounds;
        SetCursorPos(b.Left + (int)(nx * b.Width), b.Top + (int)(ny * b.Height));
    }

    public static void ClickMouse(int monitorIndex, float nx, float ny, int button, bool down)
    {
        MoveMouse(monitorIndex, nx, ny);
        uint flags = (button, down) switch
        {
            (0, true)  => MOUSEEVENTF_LEFTDOWN,
            (0, false) => MOUSEEVENTF_LEFTUP,
            (1, true)  => MOUSEEVENTF_RIGHTDOWN,
            (1, false) => MOUSEEVENTF_RIGHTUP,
            (2, true)  => MOUSEEVENTF_MIDDLEDOWN,
            (2, false) => MOUSEEVENTF_MIDDLEUP,
            _          => 0,
        };
        if (flags != 0) mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
    }

    public static void ScrollMouse(int monitorIndex, float nx, float ny, int delta)
    {
        MoveMouse(monitorIndex, nx, ny);
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta, UIntPtr.Zero);
    }

    public static void KeyEvent(ushort vk, bool down)
    {
        byte scan  = (byte)MapVirtualKey(vk, 0);
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        keybd_event((byte)vk, scan, flags, UIntPtr.Zero);
    }

    /// <summary>
    /// Force-releases every modifier key. Used as a safety net when a master
    /// disconnects mid-chord so the slave is never left with a stuck Ctrl/Shift/Alt.
    /// CapsLock/NumLock are toggles, not held keys, so they are intentionally skipped.
    /// </summary>
    public static void ReleaseModifiers()
    {
        ushort[] mods =
        {
            0x10, 0xA0, 0xA1,   // Shift, LShift, RShift
            0x11, 0xA2, 0xA3,   // Ctrl,  LCtrl,  RCtrl
            0x12, 0xA4, 0xA5,   // Alt,   LAlt,   RAlt
            0x5B, 0x5C,         // LWin,  RWin
        };
        foreach (var vk in mods) { try { KeyEvent(vk, false); } catch { } }
    }

    // Keys that require the extended-key flag for correct injection
    // (navigation cluster, numpad nav, right-hand modifiers, Win keys, etc.).
    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0xA3 or 0xA5            => true,   // RControl, RMenu (AltGr)
        0x21 or 0x22           => true,   // PageUp, PageDown
        0x23 or 0x24           => true,   // End, Home
        0x25 or 0x26 or 0x27 or 0x28 => true,   // Left, Up, Right, Down
        0x2D or 0x2E           => true,   // Insert, Delete
        0x90                   => true,   // NumLock
        0x6F                   => true,   // Numpad Divide
        0x5B or 0x5C or 0x5D   => true,   // LWin, RWin, Apps
        _                      => false,
    };
}
