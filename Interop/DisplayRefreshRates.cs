using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PartyTime.Interop;

internal static partial class DisplayRefreshRates
{
    public static unsafe Dictionary<string, double> Read()
    {
        const uint activePaths = 2;
        const uint virtualRefreshRateAware = 0x40;
        const int insufficientBuffer = 122;
        uint flags = activePaths;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            flags |= virtualRefreshRateAware;

        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // Display configuration may change between the two calls.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount) != 0
                || pathCount == 0 || modeCount == 0)
                break;

            var paths = new DisplayPath[pathCount];
            var modes = new DisplayMode[modeCount];
            int status;
            fixed (DisplayPath* pathBuffer = paths)
            fixed (DisplayMode* modeBuffer = modes)
                status = QueryDisplayConfig(flags, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);

            if (status == insufficientBuffer)
                continue;
            if (status == 0)
                ReadPaths(paths.AsSpan(0, (int)pathCount), rates);
            break;
        }
        return rates;
    }

    private static unsafe void ReadPaths(ReadOnlySpan<DisplayPath> paths, Dictionary<string, double> rates)
    {
        foreach (var path in paths)
        {
            if (path.RefreshDenominator == 0 || path.RefreshNumerator <= path.RefreshDenominator)
                continue;

            var source = new DisplaySourceName
            {
                Type = 1, // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                Size = (uint)sizeof(DisplaySourceName),
                Adapter = path.SourceAdapter,
                Id = path.SourceId
            };
            if (DisplayConfigGetDeviceInfo(ref source) != 0)
                continue;

            var name = new string(new ReadOnlySpan<char>(source.DeviceName, 32).TrimEnd('\0'));
            double refresh = (double)path.RefreshNumerator / path.RefreshDenominator;
            // Mirrored displays share a desktop source and one overlay window.
            if (!rates.TryGetValue(name, out double existing) || refresh < existing)
                rates[name] = refresh;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterId
    {
        public uint LowPart;
        public int HighPart;
    }

    // DISPLAYCONFIG_PATH_INFO layout from wingdi.h. Only the source and refresh fields are used.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct DisplayPath
    {
        [FieldOffset(0)] public AdapterId SourceAdapter;
        [FieldOffset(8)] public uint SourceId;
        [FieldOffset(48)] public uint RefreshNumerator;
        [FieldOffset(52)] public uint RefreshDenominator;
    }

    // QueryDisplayConfig requires a DISPLAYCONFIG_MODE_INFO buffer even when only paths are used.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayMode { }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DisplaySourceName
    {
        public uint Type;
        public uint Size;
        public AdapterId Adapter;
        public uint Id;
        public fixed char DeviceName[32];
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static unsafe partial int QueryDisplayConfig(uint flags, ref uint pathCount, DisplayPath* paths,
        ref uint modeCount, DisplayMode* modes, IntPtr topology);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(ref DisplaySourceName source);
}
