using System;
using System.Diagnostics;
using System.IO;

namespace PartyTime.Interop;

internal static class MacCapture
{
    public static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "Native", "macOS", "PartyTimeCapture");
        if (!File.Exists(helper))
            throw new InvalidOperationException("The macOS capture helper is missing. Build with scripts/publish.sh on macOS (requires Xcode Command Line Tools).");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }
}
