using System.IO;
using System.Runtime.Versioning;

namespace Josour.App.Services;

/// <summary>
/// Single-instance enforcement: a named mutex (<see cref="MutexName"/>) marks the running instance, and a named
/// auto-reset event (<see cref="ShowWindowEventName"/>) lets a second launch ask the first one to show its window.
/// Named kernel objects only exist on Windows; on other platforms (developer Macs) the guard is a no-op.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Global\Josour.SingleInstance";
    public const string ShowWindowEventName = @"Global\Josour.ShowWindow";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _showWindowEvent;
    private readonly ManualResetEvent _stop = new(initialState: false);
    private Thread? _listener;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, EventWaitHandle? showWindowEvent)
    {
        _mutex = mutex;
        _showWindowEvent = showWindowEvent;
    }

    /// <summary>Raised on a background thread when another launch asked us to show the window.</summary>
    public event Action? ShowWindowRequested;

    /// <summary>True when the show-window event could be created and <see cref="StartListening"/> will work.</summary>
    public bool CanReceiveShowWindow => _showWindowEvent is not null;

    /// <summary>Returns the guard when this process is the primary instance, or <c>null</c> when another instance already runs.</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SingleInstanceGuard(null, null);
        }

        return TryAcquireWindows();
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
        if (_showWindowEvent is null || _listener is not null)
        {
            return;
        }

        _listener = new Thread(Listen)
        {
            IsBackground = true,
            Name = "Josour.ShowWindowListener",
        };
        _listener.Start();
    }

    private void Listen()
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

    /// <summary>Called by a second launch: signals the primary instance to show its window.</summary>
    public static void SignalShowWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SignalShowWindowWindows();
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
        _listener?.Join(TimeSpan.FromSeconds(1));
        _showWindowEvent?.Dispose();

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
}
