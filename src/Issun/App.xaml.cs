using System.Diagnostics;
using Issun.Core;
using Issun.Core.Platform;
using Issun.Platform;
using Issun.Presentation;
using Issun.Startup;
using Issun.Tray;
using Issun.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

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
///   Issun.exe --screenshot a.png [--theme light|dark] [--expanded] [--demo-phase N] [--setup-page N [--press-next]] [--demo-tailscale N]
///                               render the window's content, or a setup page, to a PNG and exit
///
/// A release opened from anywhere but %LOCALAPPDATA%\Programs\Issun installs
/// itself there first and hands over to the installed copy (SelfInstall).
///
/// WinUI 3 since 12 Nov 2026; this is the WPF build's App, step for step.
/// </summary>
public partial class App : Application
{
    private const int StopTimeoutSeconds = 5;

    private readonly string[] _args;
    private readonly StartupOptions _options;
    private DispatcherQueue _dispatcher = null!;
    private SingleInstance? _instance;
    private IIssunHost? _host;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private SetupWindow? _setupWindow;
    private TrayIcon? _tray;
    private UiState? _uiState;
    private bool _quitting;

    public App()
    {
        _args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _options = StartupOptions.Parse(_args);
        // Follows Windows' light/dark mode and accent colour unless --theme
        // says otherwise. WinUI takes the theme only here, before anything
        // is drawn.
        switch (_options.Theme)
        {
            case ThemeChoice.Light: RequestedTheme = ApplicationTheme.Light; break;
            case ThemeChoice.Dark: RequestedTheme = ApplicationTheme.Dark; break;
        }
        AppTheme.Init(_options.Theme switch { ThemeChoice.Dark => true, ThemeChoice.Light => false, _ => null });
        InitializeComponent();
        CrashHandlers.Install(this, quiet: _options.ScreenshotPath is not null);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Until the tray icon exists, an exception here would leave a process
        // with no window, no icon and no way to quit it — running, invisible,
        // and holding the single-instance lock so the next launch does nothing
        // either. So startup failures end the process, loudly.
        try
        {
            if (_options.ScreenshotPath is not null)
            {
                // Unattended and invisible: no single-instance check (it must
                // work beside a running Issun), no log file, no tray.
                Shutdown(await Screenshot.RunAsync(HostFactory.Create(_args), _options, _dispatcher));
                return;
            }
            if (!Start(_args))
                return;
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] Issun couldn't start: {CrashHandlers.Describe(ex)}");
            var details = Log.FilePath is { } log ? $"\n\nThe details are in {log}." : "";
            DesktopShell.Message($"Issun couldn't start.\n\n{ex.Message}{details}", "Issun", DesktopShell.Icon.Error);
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

    /// <summary>
    /// Ends the process. WinUI's Exit() takes no exit code, and the sandbox
    /// test and --screenshot callers read it, so the process ends here with it.
    /// </summary>
    private static void Shutdown(int code) => Environment.Exit(code);

    /// <summary>Everything up to starting the host. False when this process is about to end.</summary>
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
        if (!_options.Demo)
            StaleUnpacks.CleanUpInBackground();

        _host = HostFactory.Create(args);
        _uiState = UiState.Load(_host.DataFolder);
        _vm = new MainViewModel(_host, _dispatcher) { OpenSetup = () => ShowSetup(afterwards: false) };
        _tray = new TrayIcon(_vm, ShowMainWindow, () => _ = QuitAsync(), OnSessionEnding);
        _instance.Listen(() => _dispatcher.TryEnqueue(ShowMainWindow));
        _instance.ListenForQuit(() => _dispatcher.TryEnqueue(() => _ = QuitAsync()));

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
            var vm = new SetupViewModel(_host, _dispatcher);
            _setupWindow = new SetupWindow(vm);
            _setupWindow.Closed += (_, _) =>
            {
                _setupWindow = null;
                _uiState?.MarkSetupShown();
                _vm?.RefreshStartWithWindows();
                if (afterwards && !_quitting)
                    ShowMainWindow();
            };
            vm.SetVisible(true);
        }
        WindowFit.Bring(_setupWindow);
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
            DesktopShell.Message(
                "Issun is already running, so this copy can't replace it yet.\n\nQuit Issun from its icon in the notification area, then open this file again.",
                "Issun", DesktopShell.Icon.Information);
            return;
        }
        try
        {
            install.Install();
        }
        catch (Exception ex)
        {
            DesktopShell.Message($"Issun couldn't install itself in {install.InstallDir}.\n\n{ex.Message}",
                "Issun", DesktopShell.Icon.Error);
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
            DesktopShell.Message($"Issun is installed, but couldn't be started.\n\n{ex.Message}\n\nOpen it from the Start menu.",
                "Issun", DesktopShell.Icon.Error);
        }
    }

    /// <summary>
    /// Settings › Apps › Uninstall. No question of Issun's own: Windows 11's
    /// Settings asks first, in its own flyout, before it runs this, so asking
    /// again was a second "are you sure" (the owner, 12 Nov 2026: "drop it").
    /// The settings, key, logs and check-in history stay, so installing again
    /// picks up where this left off.
    /// </summary>
    private static void Uninstall(SelfInstall install)
    {
        Log.Write($"[install] uninstall requested ({IssunInfo.Wire}, {install.ExePath})");
        if (!SingleInstance.AskRunningToQuit("Issun", TimeSpan.FromSeconds(10)))
        {
            DesktopShell.Message("Issun is still running. Quit it from its icon in the notification area, then uninstall it again.",
                "Uninstall Issun", DesktopShell.Icon.Information);
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
            _window.AppWindow.Closing += OnWindowClosing;
        }
        WindowFit.Bring(_window);
        _vm.SetWindowVisible(true);
        _vm.RefreshStartWithWindows();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        if (_quitting)
            return;
        e.Cancel = true;
        sender.Hide();
        _vm?.SetWindowVisible(false);
        // Closing the window applies an edited endpoint, as leaving the
        // setup page does.
        if (_vm is not null)
            _ = _vm.Pairing.ApplyPendingAsync();

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
        if (_vm is not null)
            await _vm.Pairing.ApplyPendingAsync();
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

    /// <summary>
    /// Windows is signing out or shutting down (WM_QUERYENDSESSION, which
    /// the tray icon's window receives) and will end the process shortly
    /// whatever happens. Stop cleanly while there is time, so the presence is
    /// cleared and the log says why Issun stopped.
    /// </summary>
    private void OnSessionEnding(bool shutdown)
    {
        if (_quitting)
            return;
        _quitting = true;
        Log.Write($"[ui] Windows is {(shutdown ? "shutting down" : "signing out")}; stopping");
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
        _instance?.Dispose();
    }
}
