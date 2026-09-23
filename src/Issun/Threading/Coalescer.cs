using System.Diagnostics;
using System.Windows.Threading;
using Issun.Core;

namespace Issun.Threading;

/// <summary>
/// Turns a burst of signals from any thread into at most one run of an action
/// on the UI thread per interval.
///
/// The host raises Changed from Kestrel's threads, the presence worker and
/// timers — during a skip through a playlist that is several times a second,
/// and a check-in that changes three things raises three events. The window
/// only needs the latest state, so every signal inside the interval folds into
/// one refresh at its end, and nothing is ever dropped: a signal that arrives
/// while a refresh is running schedules another.
/// </summary>
public sealed class Coalescer
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _action;
    private readonly TimeSpan _interval;
    private readonly string _name;
    private int _scheduled;
    private long _lastRun;

    public Coalescer(Dispatcher dispatcher, TimeSpan interval, string name, Action action)
    {
        _dispatcher = dispatcher;
        _interval = interval;
        _name = name;
        _action = action;
    }

    /// <summary>Safe from any thread; never blocks.</summary>
    public void Signal()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
            return;     // a run is already on its way and will read the latest state

        var last = Interlocked.Read(ref _lastRun);
        var wait = last == 0 ? TimeSpan.Zero : _interval - Stopwatch.GetElapsedTime(last);
        if (wait <= TimeSpan.Zero)
            Post();
        else
            _ = Task.Delay(wait).ContinueWith(_ => Post(), TaskScheduler.Default);
    }

    private void Post()
    {
        if (_dispatcher.HasShutdownStarted)
            return;
        // Background priority: below input and rendering, so a burst of state
        // changes can never make the window stutter under the pointer.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, Run);
    }

    private void Run()
    {
        // Cleared before the action, not after: a signal raised while the
        // action is reading state must schedule a fresh run, or its change
        // would wait for some unrelated later event to be shown.
        Interlocked.Exchange(ref _lastRun, Stopwatch.GetTimestamp());
        Volatile.Write(ref _scheduled, 0);
        try
        {
            _action();
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] {_name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
