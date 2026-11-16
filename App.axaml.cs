using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PartyTime.Interop;
using PartyTime.Views;

namespace PartyTime;

public partial class App : Application
{
    internal static AppInstance? Instance { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var controls = new ControlWindow();
            desktop.MainWindow = controls;
            Instance?.SetOpenControls(() => Dispatcher.UIThread.Post(controls.OpenControls));
            controls.ConfigureStartup(desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
