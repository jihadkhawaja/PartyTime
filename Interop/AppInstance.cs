using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PartyTime.Interop;

// Named pipes work on all three desktops and are restricted to the current user.
internal sealed class AppInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _name;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private Action? _open;
    private bool _pending;
    public bool IsPrimary { get; }

    public AppInstance(bool background)
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _name = "PartyTime-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];
        _mutex = new Mutex(false, _name);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (IsPrimary) _ = Listen();
        else if (!background)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(2000);
                client.WriteByte(1);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }

    public void SetOpenControls(Action open)
    {
        lock (_gate) { _open = open; if (_pending) { _pending = false; open(); } }
    }

    private async Task Listen()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cancellation.Token);
                byte[] message = new byte[1];
                if (await pipe.ReadAsync(message, _cancellation.Token) == 1 && message[0] == 1)
                    lock (_gate) { if (_open == null) _pending = true; else _open(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _cancellation.Dispose();
    }
}
