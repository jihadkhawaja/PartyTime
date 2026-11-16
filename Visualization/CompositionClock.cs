using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PartyTime.Visualization;

// One callback per compositor frame, rather than competing millisecond UI timers.
internal sealed class CompositionClock : IDisposable
{
    private readonly Window _window;
    private readonly Action _tick;
    private readonly Action<TimeSpan> _callback;
    private readonly DispatcherTimer _watchdog;
    private long _lastCallback;
    private bool _disposed;
    public CompositionClock(Window window, Action tick)
    {
        _window = window; _tick = tick; _callback = OnFrame;
        _lastCallback = Stopwatch.GetTimestamp();
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _watchdog.Tick += (_, _) =>
        {
            // Some remote/headless compositors stop issuing animation callbacks.
            if (!_disposed && Stopwatch.GetElapsedTime(_lastCallback).TotalMilliseconds > 100)
            {
                _watchdog.Interval = TimeSpan.FromMilliseconds(1000.0 / 60);
                _tick();
            }
        };
        _watchdog.Start();
        _window.RequestAnimationFrame(_callback);
    }
    private void OnFrame(TimeSpan time)
    {
        if (_disposed) return;
        _lastCallback = Stopwatch.GetTimestamp();
        if (_watchdog.Interval.TotalMilliseconds < 100) _watchdog.Interval = TimeSpan.FromMilliseconds(100);
        _tick();
        if (!_disposed) _window.RequestAnimationFrame(_callback);
    }
    public void Dispose() { _disposed = true; _watchdog.Stop(); }
}
