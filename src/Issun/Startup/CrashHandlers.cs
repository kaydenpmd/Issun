using System.Windows;
using Issun.Core;

namespace Issun.Startup;

/// <summary>
/// Last-chance handlers. Silent failure is the Ammy project's recurring bug —
/// three times a failure path logged nothing and the empty log read as
/// "working" — and a tray app that vanishes without a word is the same bug
/// with the lights off. So every unexpected exception is written to the log
/// with its stack, and the person is told once, instead of Issun disappearing.
/// </summary>
public static class CrashHandlers
{
    private static int _boxOpen;
    private static bool _quiet;

    /// <param name="quiet">No message boxes — for --screenshot, which runs unattended and must never put a window on the desktop.</param>
    public static void Install(Application app, bool quiet)
    {
        _quiet = quiet;

        // UI thread. Handled, so one bad refresh doesn't take the receiver down
        // with it: the host keeps running and the window keeps working.
        app.DispatcherUnhandledException += (_, e) =>
        {
            Report("on the window's thread", e.Exception, fatal: false);
            e.Handled = true;
        };

        // Any other thread. The runtime ends the process after this returns;
        // all that can be done is to leave a record and say so.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("in the background", e.ExceptionObject as Exception, fatal: e.IsTerminating);

        // A faulted task nobody awaited. Not fatal, and not worth a message
        // box, but never silent.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"[ui] unobserved task error: {Describe(e.Exception)}");
            e.SetObserved();
        };
    }

    private static void Report(string where, Exception? ex, bool fatal)
    {
        var text = ex is null ? "unknown error" : Describe(ex);
        Log.Write($"[ui] unhandled error {where}{(fatal ? ", Issun is closing" : "")}: {text}");
        if (ex?.StackTrace is { Length: > 0 } stack)
            Log.Write("[ui] " + stack.Trim().Replace(Environment.NewLine, Environment.NewLine + "[ui] "));

        if (_quiet)
            return;

        // One box at a time: an error that repeats on every refresh would
        // otherwise stack a new box four times a second. Repeats while one is
        // open are still logged above.
        if (Interlocked.Exchange(ref _boxOpen, 1) == 1)
            return;
        try
        {
            var log = Log.FilePath is { } path ? $"\n\nThe details are in {path}." : "";
            var message = fatal
                ? $"Issun hit an error it couldn't recover from and has to close.\n\n{ex?.Message}{log}"
                : $"Issun hit an error it didn't expect. It's still running, but something may not have updated.\n\n{ex?.Message}{log}";
            MessageBox.Show(message, "Issun", MessageBoxButton.OK, fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }
        catch (Exception boxError)
        {
            Log.Write($"[ui] couldn't show the error message: {boxError.Message}");
        }
        finally
        {
            Volatile.Write(ref _boxOpen, 0);
        }
    }

    /// <summary>"TypeName: message", unwrapping the AggregateException a task failure arrives in.</summary>
    public static string Describe(Exception ex)
    {
        // An AggregateException's own message is "One or more errors occurred",
        // which says nothing; the first inner one is the actual failure.
        if (ex is AggregateException { InnerExceptions.Count: > 0 } agg)
            ex = agg.Flatten().InnerExceptions[0];
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}
