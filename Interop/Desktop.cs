using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PartyTime.Interop;

internal static class Desktop
{
    private static IntPtr _display;
    private static IntPtr XDisplay => _display != IntPtr.Zero ? _display : _display = XOpenDisplay(IntPtr.Zero);
    public static string ShortcutDescription => OperatingSystem.IsMacOS()
        ? "Ctrl + Option + P · Toggle overlay (requires Input Monitoring permission)"
        : "Ctrl + Alt + P · Toggle overlay";

    public static void MakeClickThrough(Window window)
    {
        var native = window.TryGetPlatformHandle() ?? throw new InvalidOperationException("No native overlay window is available.");
        if (OperatingSystem.IsWindows())
        {
            int style = GetWindowLong(native.Handle, -20);
            SetWindowLong(native.Handle, -20, style | 0x20 | 0x08000000 | 0x80 | 0x80000);
        }
        else if (OperatingSystem.IsMacOS())
        {
            SendBool(native.Handle, Selector("setIgnoresMouseEvents:"), true);
            SendLong(native.Handle, Selector("setLevel:"), 25);
            SendLong(native.Handle, Selector("setCollectionBehavior:"), 1 | 256);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (native.HandleDescriptor != "XID" || XDisplay == IntPtr.Zero)
                throw new InvalidOperationException("Desktop overlays require X11 or XWayland. Native Wayland does not allow global desktop overlays.");
            XShapeCombineRectangles(XDisplay, native.Handle, 2, 0, 0, IntPtr.Zero, 0, 0, 0);
            XFlush(XDisplay);
        }
    }

    public static bool SetCaptureExcluded(Window window, bool exclude)
    {
        if (OperatingSystem.IsMacOS()) return true; // ScreenCaptureKit filters the PartyTime process.
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            && window.TryGetPlatformHandle() is { } native && SetWindowDisplayAffinity(native.Handle, exclude ? 0x11u : 0u);
    }

    public static Point? GetPointer()
    {
        if (OperatingSystem.IsWindows()) return GetCursorPos(out var p) ? new Point(p.X, p.Y) : null;
        if (OperatingSystem.IsMacOS())
        {
            IntPtr ev = CGEventCreate(IntPtr.Zero);
            if (ev == IntPtr.Zero) return null;
            var p = CGEventGetLocation(ev);
            CFRelease(ev);
            return new Point(p.X, p.Y);
        }
        if (OperatingSystem.IsLinux() && XDisplay != IntPtr.Zero &&
            XQueryPointer(XDisplay, XDefaultRootWindow(XDisplay), out _, out _, out int x, out int y, out _, out _, out _) != 0)
            return new Point(x, y);
        return null;
    }

    public static IDisposable WatchPartyShortcut(Action toggle) => new ShortcutWatcher(toggle);
    private static bool ShortcutDown()
    {
        if (OperatingSystem.IsWindows()) return Down(0x11) && Down(0x12) && Down(0x50);
        if (OperatingSystem.IsMacOS()) return CGEventSourceKeyState(1, 35)
            && (CGEventSourceKeyState(1, 59) || CGEventSourceKeyState(1, 62))
            && (CGEventSourceKeyState(1, 58) || CGEventSourceKeyState(1, 61));
        if (OperatingSystem.IsLinux() && XDisplay != IntPtr.Zero)
        {
            byte[] keys = new byte[32];
            XQueryKeymap(XDisplay, keys);
            bool Key(nuint symbol) { int code = XKeysymToKeycode(XDisplay, symbol); return code != 0 && (keys[code / 8] & (1 << (code % 8))) != 0; }
            return Key(0x70) && (Key(0xffe3) || Key(0xffe4)) && (Key(0xffe9) || Key(0xffea));
        }
        return false;
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private sealed class ShortcutWatcher : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private bool _wasDown;
        public ShortcutWatcher(Action toggle)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += (_, _) => { bool down = ShortcutDown(); if (down && !_wasDown) toggle(); _wasDown = down; };
            _timer.Start();
        }
        public void Dispose() => _timer.Stop();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child, out int rootX, out int rootY, out int x, out int y, out uint mask);
    [DllImport("libX11.so.6")] private static extern int XQueryKeymap(IntPtr display, [Out] byte[] keys);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, nuint symbol);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int x, int y, IntPtr rectangles, int count, int operation, int ordering);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr obj, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern void SendLong(IntPtr obj, IntPtr selector, long value);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern CGPoint CGEventGetLocation(IntPtr ev);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")][return: MarshalAs(UnmanagedType.I1)] private static extern bool CGEventSourceKeyState(int state, ushort key);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr obj);
}
