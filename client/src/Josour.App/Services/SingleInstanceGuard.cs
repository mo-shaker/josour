using System.Net.Sockets;
using System.Runtime.Versioning;
using Josour.Infrastructure;

namespace Josour.App.Services;

/// <summary>
/// Single-instance enforcement, and the channel a second launch uses to ask the first one to show its window.
/// <para>
/// Two implementations, because the primitives are not the same shape. On Windows it is a named mutex
/// (<see cref="MutexName"/>) plus a named auto-reset event (<see cref="ShowWindowEventName"/>). Elsewhere — macOS —
/// named kernel objects do not exist, so it is an exclusively-held lock file plus a Unix domain socket.
/// </para>
/// <para>
/// This is not housekeeping. Two Josours on one machine mean two control channels claiming the same device, two tray
/// icons, and two things believing they own the tunnel; the server settles that by superseding one of them
/// (docs/ws-protocol.md close code 4409), which is a worse way for the user to find out.
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Global\Josour.SingleInstance";
    public const string ShowWindowEventName = @"Global\Josour.ShowWindow";

    /// <summary>The lock file and the socket the non-Windows implementation uses, under the app's own data folder.</summary>
    public static string LockFilePath => Path.Combine(AppPaths.LocalAppDataRoot, "instance.lock");

    /// <summary>
    /// Deliberately short and in the temp directory, not beside the lock file: a Unix domain socket path is limited
    /// to about 104 bytes, and the application-support path of a user with a long name can exceed that on its own.
    /// </summary>
    public static string ShowWindowSocketPath =>
        Path.Combine(Path.GetTempPath(), $"josour-{Environment.UserName}.sock");

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _showWindowEvent;
    private readonly FileStream? _lockFile;
    private readonly Socket? _showWindowSocket;
    private readonly ManualResetEvent _stop = new(initialState: false);
    private readonly CancellationTokenSource _listenerStop = new();
    private Thread? _listener;
    private bool _disposed;

    private SingleInstanceGuard(
        Mutex? mutex, EventWaitHandle? showWindowEvent, FileStream? lockFile = null, Socket? showWindowSocket = null)
    {
        _mutex = mutex;
        _showWindowEvent = showWindowEvent;
        _lockFile = lockFile;
        _showWindowSocket = showWindowSocket;
    }

    /// <summary>Raised on a background thread when another launch asked us to show the window.</summary>
    public event Action? ShowWindowRequested;

    /// <summary>True when the show-window channel exists and <see cref="StartListening"/> will work.</summary>
    public bool CanReceiveShowWindow => _showWindowEvent is not null || _showWindowSocket is not null;

    /// <summary>Returns the guard when this process is the primary instance, or <c>null</c> when another instance already runs.</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        return OperatingSystem.IsWindows() ? TryAcquireWindows() : TryAcquirePosix();
    }

    /// <summary>
    /// macOS (and Linux): an exclusively-opened lock file stands in for the mutex, and a Unix domain socket for the
    /// event. .NET maps <see cref="FileShare.None"/> to an advisory <c>flock</c> on these platforms, and the kernel
    /// drops that lock when the process dies — including when it is killed — so a crashed instance never leaves the
    /// next launch locked out, which is exactly the property the named mutex gives on Windows.
    /// </summary>
    private static SingleInstanceGuard? TryAcquirePosix()
    {
        FileStream lockFile;
        try
        {
            Directory.CreateDirectory(AppPaths.LocalAppDataRoot);
            lockFile = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Held by the running instance.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot tell. Refusing to start over a lock file we cannot open would be worse than running.
            return new SingleInstanceGuard(null, null);
        }

        Socket? socket = null;
        try
        {
            // A socket file outlives the process that made it, so a stale one from a crash must be cleared before
            // binding or every later launch would fail to listen and silently lose "show the window".
            File.Delete(ShowWindowSocketPath);
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(ShowWindowSocketPath));
            socket.Listen(backlog: 4);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            socket?.Dispose();
            socket = null;
            // We still run; a second launch just cannot bring us to the front. Logged by App once logging is up.
        }

        return new SingleInstanceGuard(null, null, lockFile, socket);
    }

    [SupportedOSPlatform("windows")]
    private static SingleInstanceGuard? TryAcquireWindows()
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Exists in the Global namespace with an ACL we cannot open: treat as "already running".
            return null;
        }

        if (!createdNew)
        {
            // The previous instance may be in the middle of exiting; give it a moment to release the mutex.
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(500));
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // previous instance died without releasing; we own it now
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }
        }

        EventWaitHandle? showWindowEvent = null;
        try
        {
            showWindowEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowWindowEventName, out _);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // We still run; a second launch just cannot bring us to the front. Logged by App once logging is up.
        }

        return new SingleInstanceGuard(mutex, showWindowEvent);
    }

    /// <summary>Starts the background thread that waits for show-window requests from later launches.</summary>
    public void StartListening()
    {
        if (_listener is not null || !CanReceiveShowWindow)
        {
            return;
        }

        _listener = new Thread(_showWindowEvent is not null ? ListenOnEvent : ListenOnSocket)
        {
            IsBackground = true,
            Name = "Josour.ShowWindowListener",
        };
        _listener.Start();
    }

    private void ListenOnEvent()
    {
        var handles = new WaitHandle[] { _stop, _showWindowEvent! };
        while (true)
        {
            var index = WaitHandle.WaitAny(handles);
            if (index == 0)
            {
                return;
            }

            ShowWindowRequested?.Invoke();
        }
    }

    /// <summary>One connection is one request; nothing is read from it, so a stuck peer cannot stall the loop.</summary>
    private void ListenOnSocket()
    {
        var socket = _showWindowSocket!;
        while (!_listenerStop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = socket.Accept();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return; // Dispose closed the socket
            }

            client.Dispose();
            if (!_listenerStop.IsCancellationRequested)
            {
                ShowWindowRequested?.Invoke();
            }
        }
    }

    /// <summary>Called by a second launch: signals the primary instance to show its window.</summary>
    public static void SignalShowWindow()
    {
        if (OperatingSystem.IsWindows())
        {
            SignalShowWindowWindows();
            return;
        }

        SignalShowWindowPosix();
    }

    /// <summary>Connect and disconnect: the connection itself is the message.</summary>
    private static void SignalShowWindowPosix()
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            // Bounded: this runs in a process whose only remaining job is to exit, and a primary instance that is
            // too busy to accept is not worth waiting on.
            client.Connect(new UnixDomainSocketEndPoint(ShowWindowSocketPath));
        }
        catch (Exception ex) when (ex is SocketException or IOException or PlatformNotSupportedException)
        {
            // The primary instance exists but cannot be signalled; it stays where it is.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SignalShowWindowWindows()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Nothing else to do: the primary instance exists but cannot be signalled.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();
        _listenerStop.Cancel();

        // Closing the listening socket is what unblocks Accept(); the flag alone would leave the thread parked there.
        _showWindowSocket?.Dispose();
        _listener?.Join(TimeSpan.FromSeconds(1));
        _showWindowEvent?.Dispose();
        _listenerStop.Dispose();

        if (_lockFile is not null)
        {
            // The advisory lock goes with the handle. The file itself stays: it is a lock, not a record, and
            // deleting it would race a launch that is opening it at this moment.
            _lockFile.Dispose();
            TryDeleteSocketFile();
        }

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex(); // Dispose runs on the STA thread that acquired it (Program.Main).
            }
            catch (ApplicationException)
            {
                // Not owned by this thread; disposing still closes the handle.
            }

            _mutex.Dispose();
        }

        _stop.Dispose();
    }

    private static void TryDeleteSocketFile()
    {
        try
        {
            File.Delete(ShowWindowSocketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover socket file is cleared by the next launch before it binds.
        }
    }
}
