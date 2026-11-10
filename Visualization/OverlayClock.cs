using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace PartyTime.Visualization;

internal sealed partial class OverlayClock : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WaitHandle _timer;
    private readonly RegisteredWaitHandle _wait;
    private readonly Action _tick;
    private int _pending;
    private int _disposed;

    public OverlayClock(Action render)
    {
        _tick = () =>
        {
            Volatile.Write(ref _pending, 0);
            if (Volatile.Read(ref _disposed) == 0)
                render();
        };

        const uint highResolution = 2;
        const uint synchronizeAndModify = 0x00100002;
        var handle = CreateWaitableTimer(IntPtr.Zero, null, highResolution, synchronizeAndModify);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateWaitableTimer(IntPtr.Zero, null, 0, synchronizeAndModify);
        }
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        _timer = new TimerHandle(handle);
        try
        {
            _wait = ThreadPool.UnsafeRegisterWaitForSingleObject(_timer, (_, _) => QueueFrame(),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch
        {
            _timer.Dispose();
            throw;
        }
    }

    public void Schedule(long deadline)
    {
        long due = -Math.Max(1, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline).Ticks);
        if (!SetWaitableTimer(_timer.SafeWaitHandle, in due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private void QueueFrame()
    {
        // A settings change can rearm the timer before a previously queued frame runs.
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _pending, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Background, _tick);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _wait.Unregister(null);
        _timer.Dispose();
    }

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimer(IntPtr attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, in long due, int period,
        IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
