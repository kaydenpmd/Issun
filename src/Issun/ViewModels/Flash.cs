using Microsoft.UI.Dispatching;

namespace Issun.ViewModels;

/// <summary>
/// A few words beside a button that clear themselves — "Copied", "Saved".
/// Confirmation that belongs to one moment, so it must not stay on screen
/// asserting it after the moment has passed. UI thread only.
/// </summary>
public sealed class Flash : Observable
{
    private readonly DispatcherQueueTimer _timer;
    private string? _text;

    public Flash(DispatcherQueue dispatcher, TimeSpan? duration = null)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = duration ?? TimeSpan.FromSeconds(3);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Text = null;
        };
    }

    public string? Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    public void Show(string text)
    {
        Text = text;
        _timer.Stop();
        _timer.Start();
    }

    public void Clear()
    {
        _timer.Stop();
        Text = null;
    }
}
