using System.Runtime.InteropServices;
using System.Security.Principal;
using Issun.Core;

namespace Issun.Startup;

/// <summary>
/// One Issun per signed-in user. Two copies would fight over the port and
/// both talk to Discord, the same failure the Ammy rename caused when the old
/// app and the new one both pushed to the relay.
///
/// A second launch — double-clicking the .exe while Issun sits in the tray,
/// usually — sets a named event and exits; the first copy is waiting on that
/// event and brings its window forward.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequest;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequest)
    {
        _mutex = mutex;
        _showRequest = showRequest;
    }

    /// <summary>
    /// Becomes the one instance, or — when another already is — asks it to
    /// show its window (unless <paramref name="signalExisting"/> is false) and
    /// returns null.
    /// </summary>
    /// <param name="scope">"Issun", or "Issun-demo" so a demo can run beside the real one.</param>
    public static SingleInstance? TryAcquire(string scope, bool signalExisting)
    {
        // Local\ is this logon session; the SID separates two users switched
        // into the same session.
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var baseName = $@"Local\{scope}-{user}";

        // The event is created (or opened) before the mutex is tested, so a
        // second launch arriving while the first is still starting finds the
        // event already there. It is auto-reset: a signal set before the first
        // copy begins waiting stays set until it does.
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, baseName + "-show");
        var mutex = new Mutex(initiallyOwned: true, baseName, out var createdNew);

        if (createdNew)
            return new SingleInstance(mutex, showRequest);

        if (signalExisting)
        {
            // The launch the person just made is the foreground process, and
            // Windows lets only the foreground process hand focus on. Without
            // this the first copy's window would flash in the taskbar instead
            // of coming forward.
            AllowSetForegroundWindow(AsfwAny);
            showRequest.Set();
        }
        mutex.Dispose();
        showRequest.Dispose();
        return null;
    }

    /// <summary>Calls <paramref name="onShowRequested"/> on a thread-pool thread each time another launch asks.</summary>
    public void Listen(Action onShowRequested)
    {
        _wait ??= ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) =>
        {
            try { onShowRequested(); }
            catch (Exception ex) { Log.Write($"[ui] couldn't show the window for a second launch: {ex.Message}"); }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Idempotent: Quit and application exit both call it.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _wait?.Unregister(null);
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* not owned by this thread: closing the handle releases it */ }
        _mutex.Dispose();
        _showRequest.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
