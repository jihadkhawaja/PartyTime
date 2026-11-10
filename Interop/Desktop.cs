using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace PartyTime.Interop;

internal static partial class Desktop
{
    public const int HotkeyMessage = 0x0312;
    public const int PartyHotkey = 1;

    public static void MakeClickThrough(IntPtr window)
    {
        const int extendedStyle = -20;
        const int transparent = 0x20;
        const int noActivate = 0x08000000;
        const int toolWindow = 0x80;
        int styles = GetWindowLong(window, extendedStyle);
        SetWindowLong(window, extendedStyle, styles | transparent | noActivate | toolWindow);
    }

    public static void PlaceOverlay(IntPtr window, Rectangle bounds)
    {
        const int topmost = -1;
        const uint noActivate = 0x10;
        if (!SetWindowPos(window, topmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, noActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static bool RegisterPartyHotkey(IntPtr window)
        => RegisterHotKey(window, PartyHotkey, 0x4003, 0x50); // No repeat, Ctrl + Alt + P

    public static bool SetCaptureExcluded(IntPtr window, bool exclude)
        => SetWindowDisplayAffinity(window, exclude ? 0x11u : 0u);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(IntPtr window, int index, int value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr window, int id);
}
