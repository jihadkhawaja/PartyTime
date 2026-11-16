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
        .UsePlatformDetect()
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software] })
        .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] })
        .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software] })
        .LogToTrace();
}
