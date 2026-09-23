using System.ComponentModel;
using System.Windows;
using Issun.Core;
using Issun.Startup;
using Issun.Tray;
using Issun.ViewModels;

namespace Issun;

/// <summary>
/// Startup and shutdown. Issun lives in the tray: the window is created on
/// first open, closing it only hides it, and the process ends only through
/// Quit (or Windows signing out).
///
///   Issun.exe                   window and tray
///   Issun.exe --background      tray only — what Start with Windows runs
///   Issun.exe --demo            made-up data; binds nothing, writes nothing real
///   Issun.exe --screenshot a.png [--theme light|dark] [--expanded] [--demo-phase N]
///                               render the window's content to a PNG and exit
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private StartupOptions _options = new();
    private SingleInstance? _instance;
    private IIssunHost? _host;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private UiState? _uiState;
    private bool _quitting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _options = StartupOptions.Parse(e.Args);
        ThemeMode = _options.Theme switch
        {
            ThemeChoice.Light => ThemeMode.Light,
            ThemeChoice.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,      // follows Windows' light/dark and accent colour
        };
        CrashHandlers.Install(this, quiet: _options.ScreenshotPath is not null);

        if (_options.ScreenshotPath is not null)
        {
            // Unattended and invisible: no single-instance check (it must work
            // beside a running Issun), no log file, no tray, no window.
            var code = await Screenshot.RunAsync(HostFactory.Create(e.Args), _options, Dispatcher);
            Shutdown(code);
            return;
        }

        // A second launch by hand means "show me Issun"; a second --background
        // launch is the sign-in entry firing while Issun already runs, and
        // popping the window up at the person would be wrong.
        _instance = SingleInstance.TryAcquire(_options.Demo ? "Issun-demo" : "Issun", signalExisting: !_options.Background);
        if (_instance is null)
        {
            Shutdown(0);
            return;
        }

        // The demo must not write into the real data folder, so its lines stay
        // in memory and in the window's Log section.
        if (!_options.Demo)
            Log.UseFile(Paths.LogFile);
        Log.Write($"[ui] {IssunInfo.Wire} starting{(_options.Background ? " in the tray" : "")}");
        foreach (var problem in _options.Problems)
            Log.Write($"[ui] ignoring the command line: {problem}");

        _host = HostFactory.Create(e.Args);
        _uiState = UiState.Load(_host.DataFolder);
        _vm = new MainViewModel(_host, Dispatcher);
        _tray = new TrayIcon(_vm, ShowMainWindow, () => _ = QuitAsync());
        _instance.Listen(() => Dispatcher.BeginInvoke(ShowMainWindow));

        if (!_options.Background)
            ShowMainWindow();

        try
        {
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            // The window stays up (or the tray does) so the person can see why
            // and change the setting that caused it — a port already in use,
            // most likely, while relay.py is still running.
            Log.Write($"[ui] Issun couldn't start: {CrashHandlers.Describe(ex)}");
            _vm.ReportStartupFailure(ex);
            if (_options.Background)
                ShowMainWindow();
        }
    }

    private void ShowMainWindow()
    {
        if (_quitting || _vm is null)
            return;
        if (_window is null)
        {
            _window = new MainWindow(_vm);
            _window.Closing += OnWindowClosing;
        }
        if (!_window.IsVisible)
            _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting)
            return;
        e.Cancel = true;
        _window?.Hide();

        // Said once, ever. Without it the first close looks like quitting, and
        // the person wonders later why Discord still shows their music.
        if (_uiState is { TrayHintShown: false })
        {
            _tray?.ShowHint("Issun is still running",
                "It keeps passing Ammy's music on to Discord from here. Right-click this icon to quit.");
            _uiState.MarkTrayHintShown();
        }
    }

    private async Task QuitAsync()
    {
        if (_quitting)
            return;
        _quitting = true;
        Log.Write("[ui] quitting");

        _window?.Close();
        _tray?.Dispose();
        _vm?.Dispose();
        await StopHostAsync();
        _instance?.Dispose();
        Shutdown(0);
    }

    /// <summary>
    /// Disposes the host off the UI thread, and gives up after a few seconds:
    /// a Discord pipe that never answers must not leave Issun unable to quit.
    /// </summary>
    private async Task StopHostAsync()
    {
        if (_host is not { } host)
            return;
        _host = null;
        try
        {
            await Task.Run(() => host.DisposeAsync().AsTask()).WaitAsync(StopTimeout);
        }
        catch (TimeoutException)
        {
            Log.Write($"[ui] still stopping after {StopTimeout.TotalSeconds:0}s; closing anyway");
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] error while stopping: {CrashHandlers.Describe(ex)}");
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is signing out or shutting down and will end the process
        // shortly whatever happens. Stop cleanly while there is time, so the
        // presence is cleared and the log says why Issun stopped.
        base.OnSessionEnding(e);
        if (_quitting)
            return;
        _quitting = true;
        Log.Write($"[ui] Windows is {(e.ReasonSessionEnding == ReasonSessionEnding.Shutdown ? "shutting down" : "signing out")}; stopping");
        _tray?.Dispose();
        _vm?.Dispose();
        if (_host is { } host)
        {
            _host = null;
            // Blocking the UI thread is deliberate here: once this returns
            // Windows may end the process at any moment. Run on the pool so a
            // continuation that wants this thread can't deadlock the wait.
            try
            {
                if (!Task.Run(() => host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3)))
                    Log.Write("[ui] still stopping after 3s; Windows may end Issun first");
            }
            catch (Exception ex)
            {
                Log.Write($"[ui] error while stopping: {CrashHandlers.Describe(ex)}");
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
