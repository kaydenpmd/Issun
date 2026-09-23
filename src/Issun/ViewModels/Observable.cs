using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Issun.Core;

namespace Issun.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A button's action. Runs on the UI thread.</summary>
public sealed class Command(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();
}

/// <summary>
/// A button whose action awaits the host. Disabled while it runs, so a second
/// click can't start a second Funnel request or a second key replacement, and
/// every failure is logged and handed to <paramref name="onError"/> rather than
/// disappearing into an unobserved task.
/// </summary>
public sealed class AsyncCommand(string what, Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null)
    : Observable, ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    /// <summary>Bindable, for a "Checking…" label or a progress ring beside the button.</summary>
    public bool IsRunning
    {
        get => _running;
        private set => Set(ref _running, value);
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    /// <summary>For a <c>canExecute</c> that depends on state the command can't see change.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async void Execute(object? parameter)
    {
        if (_running)
            return;
        IsRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] {what} failed: {ex.GetType().Name}: {ex.Message}");
            onError(ex);
        }
        finally
        {
            IsRunning = false;
            RaiseCanExecuteChanged();
        }
    }
}
