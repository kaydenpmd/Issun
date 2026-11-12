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
    private readonly EventWaitHandle _quitRequest;
    private RegisteredWaitHandle? _wait;
    private RegisteredWaitHandle? _quitWait;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequest, EventWaitHandle quitRequest)
    {
        _mutex = mutex;
        _showRequest = showRequest;
        _quitRequest = quitRequest;
    }

    private static string BaseName(string scope)
    {
        // Local\ is this logon session; the SID separates two users switched
        // into the same session.
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return $@"Local\{scope}-{user}";
    }

    /// <summary>
    /// Asks a running Issun to quit, for installing over it or uninstalling
    /// it, and waits until it has. False when it is still running after
    /// <paramref name="timeout"/>: builds from before the setup window don't
    /// listen for this, and have to be quit from the tray.
    /// </summary>
    public static bool AskRunningToQuit(string scope, TimeSpan timeout)
    {
        var baseName = BaseName(scope);
        if (!IsRunning(baseName))
            return true;
        if (EventWaitHandle.TryOpenExisting(baseName + "-quit", out var quit))
        {
            using (quit)
                quit.Set();
            Log.Write("[install] asked the running Issun to quit");
        }
        else
        {
            Log.Write("[install] the running Issun is a build that can't be asked to quit");
        }
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            if (!IsRunning(baseName))
                return true;
        }
        Log.Write($"[install] the running Issun was still running after {timeout.TotalSeconds:0} s");
        return false;
    }

    private static bool IsRunning(string baseName)
    {
        if (!Mutex.TryOpenExisting(baseName, out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>
    /// Becomes the one instance, or — when another already is — asks it to
    /// show its window (unless <paramref name="signalExisting"/> is false) and
    /// returns null.
    /// </summary>
    /// <param name="scope">"Issun", or "Issun-demo" so a demo can run beside the real one.</param>
    public static SingleInstance? TryAcquire(string scope, bool signalExisting)
    {
        var baseName = BaseName(scope);

        // The event is created (or opened) before the mutex is tested, so a
        // second launch arriving while the first is still starting finds the
        // event already there. It is auto-reset: a signal set before the first
        // copy begins waiting stays set until it does.
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, baseName + "-show");
        var mutex = new Mutex(initiallyOwned: true, baseName, out var createdNew);

        if (createdNew)
            return new SingleInstance(mutex, showRequest, new EventWaitHandle(false, EventResetMode.AutoReset, baseName + "-quit"));

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

    /// <summary>Calls <paramref name="onQuitRequested"/> on a thread-pool thread when a newer copy is installing over this one, or Issun is being uninstalled.</summary>
    public void ListenForQuit(Action onQuitRequested)
    {
        _quitWait ??= ThreadPool.RegisterWaitForSingleObject(_quitRequest, (_, _) =>
        {
            Log.Write("[ui] asked to quit by another copy of Issun (an update or uninstall)");
            try { onQuitRequested(); }
            catch (Exception ex) { Log.Write($"[ui] couldn't quit when asked: {ex.Message}"); }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>Idempotent: Quit and application exit both call it.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _wait?.Unregister(null);
        _quitWait?.Unregister(null);
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* not owned by this thread: closing the handle releases it */ }
        _mutex.Dispose();
        _showRequest.Dispose();
        _quitRequest.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
