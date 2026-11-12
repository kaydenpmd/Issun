using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Issun.Core;
using Issun.Core.Platform;
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
///   Issun.exe --uninstall       what Settings › Apps runs: asks, then removes Issun
///   Issun.exe --screenshot a.png [--theme light|dark] [--expanded] [--demo-phase N] [--setup-page N]
///                               render the window's content, or a setup page, to a PNG and exit
///
/// A release opened from anywhere but %LOCALAPPDATA%\Programs\Issun installs
/// itself there first and hands over to the installed copy (SelfInstall).
/// </summary>
public partial class App : Application
{
    private const int StopTimeoutSeconds = 5;

    private StartupOptions _options = new();
    private SingleInstance? _instance;
    private IIssunHost? _host;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private SetupWindow? _setupWindow;
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

        // Until the tray icon exists, an exception here would leave a process
        // with no window, no icon and no way to quit it — running, invisible,
        // and holding the single-instance lock so the next launch does nothing
        // either. So startup failures end the process, loudly.
        try
        {
            if (_options.ScreenshotPath is not null)
            {
                // Unattended and invisible: no single-instance check (it must
                // work beside a running Issun), no log file, no tray, no window.
                Shutdown(await Screenshot.RunAsync(HostFactory.Create(e.Args), _options, Dispatcher));
                return;
            }
            if (!Start(e.Args))
                return;
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] Issun couldn't start: {CrashHandlers.Describe(ex)}");
            if (_options.ScreenshotPath is null)
            {
                var details = Log.FilePath is { } log ? $"\n\nThe details are in {log}." : "";
                MessageBox.Show($"Issun couldn't start.\n\n{ex.Message}{details}",
                    "Issun", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            _tray?.Dispose();
            await StopHostAsync();
            Shutdown(1);
            return;
        }

        try
        {
            await _host!.StartAsync();
        }
        catch (Exception ex)
        {
            // The window stays up (or comes up) so the person can see why and
            // change the setting that caused it — a port already in use, most
            // likely, while relay.py is still running.
            Log.Write($"[ui] Issun couldn't start: {CrashHandlers.Describe(ex)}");
            _vm!.ReportStartupFailure(ex);
            ShowMainWindow();
        }
    }

    /// <summary>Everything up to starting the host. False when another Issun is already running.</summary>
    private bool Start(string[] args)
    {
        if (!_options.Demo)
        {
            Log.UseFile(Paths.LogFile);
            var install = new SelfInstall();
            if (_options.Uninstall)
            {
                Uninstall(install);
                Shutdown(0);
                return false;
            }
            if (install.ShouldInstall)
            {
                InstallAndHandOver(install, args);
                Shutdown(0);
                return false;
            }
            install.EnsureRegistered();
        }

        // A second launch by hand means "show me Issun"; a second --background
        // launch is the sign-in entry firing while Issun already runs, and
        // popping the window up at the person would be wrong.
        _instance = SingleInstance.TryAcquire(_options.Demo ? "Issun-demo" : "Issun", signalExisting: !_options.Background);
        if (_instance is null)
        {
            Shutdown(0);
            return false;
        }

        // The demo must not write into the real data folder, so its lines stay
        // in memory and in the window's Log section; the real log file was
        // opened above, before installing.
        Log.Write($"[ui] {IssunInfo.Wire} starting{(_options.Background ? " in the tray" : "")}");
        foreach (var problem in _options.Problems)
            Log.Write($"[ui] ignoring the command line: {problem}");

        _host = HostFactory.Create(args);
        _uiState = UiState.Load(_host.DataFolder);
        _vm = new MainViewModel(_host, Dispatcher) { OpenSetup = () => ShowSetup(afterwards: false) };
        _tray = new TrayIcon(_vm, ShowMainWindow, () => _ = QuitAsync());
        _instance.Listen(() => Dispatcher.BeginInvoke(ShowMainWindow));
        _instance.ListenForQuit(() => Dispatcher.BeginInvoke(() => _ = QuitAsync()));

        // A first run opens the setup window instead of the main one, once
        // ever; the main window follows when setup closes.
        if (!_options.Background && _host.FirstRun && !_uiState.SetupShown)
        {
            Log.Write("[ui] first run: opening setup");
            ShowSetup(afterwards: true);
        }
        else if (!_options.Background)
        {
            ShowMainWindow();
        }
        return true;
    }

    /// <param name="afterwards">Open the main window when setup closes: the first run, where setup came first.</param>
    private void ShowSetup(bool afterwards)
    {
        if (_quitting || _host is null)
            return;
        if (_setupWindow is null)
        {
            _setupWindow = new SetupWindow(new SetupViewModel(_host, Dispatcher));
            _setupWindow.Closed += (_, _) =>
            {
                _setupWindow = null;
                _uiState?.MarkSetupShown();
                _vm?.DismissBannerCommand.Execute(null);
                if (afterwards && !_quitting)
                    ShowMainWindow();
            };
            _setupWindow.Show();
        }
        if (_setupWindow.WindowState == WindowState.Minimized)
            _setupWindow.WindowState = WindowState.Normal;
        _setupWindow.Activate();
    }

    /// <summary>
    /// This copy is a download: install it, quitting a running Issun first,
    /// and start the installed copy in its place. Every failure is shown,
    /// because nothing else will explain why opening the file did nothing.
    /// </summary>
    private static void InstallAndHandOver(SelfInstall install, string[] args)
    {
        Log.Write($"[install] {IssunInfo.Wire} opened from {install.ExePath}; installing to {install.InstallDir}");
        if (!SingleInstance.AskRunningToQuit("Issun", TimeSpan.FromSeconds(10)))
        {
            MessageBox.Show(
                "Issun is already running, so this copy can't replace it yet.\n\nQuit Issun from its icon in the notification area, then open this file again.",
                "Issun", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            install.Install();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Issun couldn't install itself in {install.InstallDir}.\n\n{ex.Message}",
                "Issun", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        try
        {
            var start = new ProcessStartInfo(install.InstalledExe) { UseShellExecute = false };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            Process.Start(start);
            Log.Write($"[install] started {install.InstalledExe}");
        }
        catch (Exception ex)
        {
            Log.Write($"[install] couldn't start {install.InstalledExe}: {ex.Message}");
            MessageBox.Show($"Issun is installed, but couldn't be started.\n\n{ex.Message}\n\nOpen it from the Start menu.",
                "Issun", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Settings › Apps › Uninstall. The settings, key, logs and check-in
    /// history stay, so installing again picks up where this left off; the
    /// message says so, since nothing else would.
    /// </summary>
    private static void Uninstall(SelfInstall install)
    {
        Log.Write($"[install] uninstall requested ({IssunInfo.Wire}, {install.ExePath})");
        var answer = MessageBox.Show(
            "Uninstall Issun?\n\nYour settings, key and logs stay on this PC, so installing Issun again picks up where it left off.",
            "Uninstall Issun", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            Log.Write("[install] uninstall cancelled");
            return;
        }
        if (!SingleInstance.AskRunningToQuit("Issun", TimeSpan.FromSeconds(10)))
        {
            MessageBox.Show("Issun is still running. Quit it from its icon in the notification area, then uninstall it again.",
                "Uninstall Issun", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        install.Uninstall(new Autostart(), new StartMenuShortcut());
        Log.Write("[install] uninstalled");
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
                "It keeps passing your music on to Discord from here. Right-click this icon to quit.");
            _uiState.MarkTrayHintShown();
        }
    }

    private async Task QuitAsync()
    {
        if (_quitting)
            return;
        _quitting = true;
        Log.Write("[ui] quitting");

        _setupWindow?.Close();
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
            await Task.Run(() => host.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(StopTimeoutSeconds));
        }
        catch (TimeoutException)
        {
            Log.Write($"[ui] still stopping after {StopTimeoutSeconds}s; closing anyway");
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
