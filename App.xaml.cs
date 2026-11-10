using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using PartyTime.Interop;
using PartyTime.Views;

namespace PartyTime;

public partial class App : Application
{
    private EventWaitHandle? _openControlsSignal;
    private RegisteredWaitHandle? _openControlsWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool startInTray = Array.Exists(e.Args, argument => argument == WindowsStartup.TrayArgument);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\PartyTime.OpenControls.51E2D71B-A38F-4A3B-8F97-2C3F3C02C00E", out bool firstInstance);
        if (!firstInstance)
        {
            if (!startInTray)
                signal.Set();
            signal.Dispose();
            Shutdown();
            return;
        }

        _openControlsSignal = signal;
        var controls = new ControlWindow();
        MainWindow = controls;
        if (startInTray)
            new WindowInteropHelper(controls).EnsureHandle();
        else
            controls.Show();

        _openControlsWait = ThreadPool.RegisterWaitForSingleObject(signal,
            (_, _) => Dispatcher.BeginInvoke(controls.OpenControls), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _openControlsWait?.Unregister(null);
        _openControlsSignal?.Dispose();
        base.OnExit(e);
    }
}
