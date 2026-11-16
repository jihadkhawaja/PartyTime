using System;
using System.IO;
using System.Linq;
using System.Security;

namespace PartyTime.Interop;

internal static class AppStartup
{
    public const string TrayArgument = "--startup";
    private static string EntryPath => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.partytime.app.plist")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart", "partytime.desktop");

    public static bool IsEnabled() => OperatingSystem.IsWindows() ? WindowsStartup.IsEnabled() : File.Exists(EntryPath);

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows()) { WindowsStartup.SetEnabled(enabled); return; }
        if (!enabled) { File.Delete(EntryPath); return; }
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the PartyTime executable.");
        string[] command = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [executable, Path.GetFullPath(Environment.GetCommandLineArgs()[0]), TrayArgument] : [executable, TrayArgument];
        string text;
        if (OperatingSystem.IsMacOS())
        {
            text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>com.partytime.app</string><key>ProgramArguments</key><array>"
                + string.Concat(command.Select(arg => "<string>" + SecurityElement.Escape(arg) + "</string>"))
                + "</array><key>RunAtLoad</key><true/></dict></plist>";
        }
        else
        {
            string Quote(string arg) => "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
            text = "[Desktop Entry]\nType=Application\nName=PartyTime\nExec=" + string.Join(" ", command.Select(Quote)) + "\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath, text);
    }
}
