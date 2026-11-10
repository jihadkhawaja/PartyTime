using System;
using System.IO;
using Microsoft.Win32;

namespace PartyTime.Interop;

internal static class WindowsStartup
{
    public const string TrayArgument = "--startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "PartyTime";
    private const int MaximumCommandLength = 260;

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(EntryName) is string { Length: > 0 };
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(EntryName, throwOnMissingValue: false);
            return;
        }

        string command = GetLaunchCommand();
        using var startup = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("Windows could not open the startup registry key.");
        startup.SetValue(EntryName, command, RegistryValueKind.String);
    }

    private static string GetLaunchCommand()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("PartyTime could not find its executable.");
        string command = $"\"{executable}\"";
        if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            string assembly = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
            if (!File.Exists(assembly))
                throw new InvalidOperationException("PartyTime could not find its application file.");
            command += $" \"{assembly}\"";
        }

        command += $" {TrayArgument}";
        if (command.Length > MaximumCommandLength)
            throw new InvalidOperationException("Move PartyTime to a folder with a shorter path and try again.");
        return command;
    }
}
