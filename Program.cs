using System;
using Avalonia;
using Avalonia.Threading;
using PartyTime.Interop;

namespace PartyTime;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new AppInstance(Array.Exists(args, arg => arg == AppStartup.TrayArgument));
        if (!instance.IsPrimary) return;
        App.Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect().LogToTrace();
}
