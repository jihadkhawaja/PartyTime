using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace PartyTime.Interop;

internal static class ScreenRefreshRate
{
    public static double Read(Screen screen, Dictionary<string, double>? windowsRates)
    {
        if (OperatingSystem.IsWindows() && windowsRates != null)
        {
            var bounds = screen.Bounds;
            var monitor = MonitorFromPoint(new NativePoint { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 }, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info) && windowsRates.TryGetValue(info.Device, out double rate)) return rate;
            return 60;
        }
        if (OperatingSystem.IsMacOS() && screen.TryGetPlatformHandle() is { HandleDescriptor: "CGDirectDisplayID" } handle)
        {
            var mode = CGDisplayCopyDisplayMode((uint)handle.Handle.ToInt64());
            if (mode != IntPtr.Zero)
            {
                try { double rate = CGDisplayModeGetRefreshRate(mode); if (rate > 1 && double.IsFinite(rate)) return rate; }
                finally { CGDisplayModeRelease(mode); }
            }
        }
        return 60;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern void CGDisplayModeRelease(IntPtr mode);
}
