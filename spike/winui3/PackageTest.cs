using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Win32;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel;

namespace IssunSpike;

/// <summary>
/// --package-test dir --port N --pipe name [--leave-startup-on]: what Issun
/// needs from Windows, checked from inside the MSIX package. Writes
/// dir\package-test.json, then keeps the listener up until dir\stop appears
/// (at most 90 s) so the test outside can reach it.
/// </summary>
internal static class PackageTest
{
    public static void LogActivation()
    {
        try
        {
            var args = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            SpikeLog.Write($"[activation] kind {args.Kind}");
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"[activation] unavailable: {ex.Message}");
        }
        try { SpikeLog.Write($"[package] {Package.Current.Id.FullName}"); }
        catch { SpikeLog.Write("[package] none (running unpackaged)"); }
    }

    public static async Task<int> RunAsync(string dir, int port, string pipe, bool leaveStartupOn)
    {
        Directory.CreateDirectory(dir);
        var r = new Dictionary<string, object?>();
        void Note(string key, object? value) { r[key] = value; SpikeLog.Write($"[pkgtest] {key}: {value}"); }

        try
        {
            var p = Package.Current;
            Note("package", p.Id.FullName);
            Note("installedLocation", p.InstalledLocation.Path);
            Note("familyName", p.Id.FamilyName);
            Note("applicationDataLocalFolder", Windows.Storage.ApplicationData.Current.LocalFolder.Path);
        }
        catch (Exception ex) { Note("package", "none: " + ex.Message); }

        // Where writes land. Issun keeps settings in %APPDATA%\Issun and logs in
        // %LOCALAPPDATA%\Issun; MSIX may redirect both into the package's folder.
        foreach (var (key, folder) in new[] { ("localAppData", Environment.SpecialFolder.LocalApplicationData), ("roamingAppData", Environment.SpecialFolder.ApplicationData) })
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(folder), "IssunSpikeProbe", "probe.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "written from inside the package");
                Note(key + "ProbeAsSeenInside", path);
            }
            catch (Exception ex) { Note(key + "Probe", "failed: " + ex.Message); }
        }
        // And whether the app can still read a file someone else put in the
        // real %APPDATA% before it was installed (the test plants one).
        var planted = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IssunPlanted", "settings.json");
        Note("plantedSettingsReadable", File.Exists(planted) ? File.ReadAllText(planted).Trim() : "not found");
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\IssunSpikeProbe")) k.SetValue("written", "from inside the package");
            Note("hkcuWrite", "ok");
        }
        catch (Exception ex) { Note("hkcuWrite", "failed: " + ex.Message); }

        // The listener, as Issun's RelayServer runs it: Kestrel on 127.0.0.1.
        WebApplication? web = null;
        try
        {
            var b = WebApplication.CreateSlimBuilder();
            b.WebHost.UseUrls($"http://127.0.0.1:{port}");
            web = b.Build();
            web.MapGet("/health", () => "ok");
            await web.StartAsync();
            Note("listener", $"listening on 127.0.0.1:{port}");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Note("listenerSelfGet", await http.GetStringAsync($"http://127.0.0.1:{port}/health"));
        }
        catch (Exception ex) { Note("listener", "failed: " + ex.Message); }

        // A named pipe, as Issun reaches Discord's: client to a server the test hosts.
        try
        {
            using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
            await c.ConnectAsync(15000);
            using var w = new StreamWriter(c) { AutoFlush = true };
            using var rd = new StreamReader(c);
            await w.WriteLineAsync("hello from the package");
            Note("pipeReply", await rd.ReadLineAsync());
        }
        catch (Exception ex)
        {
            // Closing after the server hangs up can throw once the reply is in.
            if (r.ContainsKey("pipeReply")) Note("pipeCloseError", ex.Message);
            else Note("pipeReply", "failed: " + ex.Message);
        }

        // Other programs, as Issun runs tailscale and winget.
        Note("runTailscaleByPath", Run(@"C:\Program Files\Tailscale\tailscale.exe", "version"));
        Note("runTailscaleOnPath", Run("tailscale", "version"));
        Note("runWinget", Run("winget", "--version"));
        Note("runCmd", Run("cmd.exe", "/c ver"));

        // Start with Windows: the StartupTask in place of the Run key.
        try
        {
            var task = await StartupTask.GetAsync("IssunSpikeStartup");
            Note("startupInitial", task.State.ToString());
            Note("startupAfterRequestEnable", (await task.RequestEnableAsync()).ToString());
            task.Disable();
            Note("startupAfterDisable", task.State.ToString());
            if (leaveStartupOn)
                Note("startupLeftOn", (await task.RequestEnableAsync()).ToString());
        }
        catch (Exception ex) { Note("startupTask", "failed: " + ex.Message); }

        File.WriteAllText(Path.Combine(dir, "package-test.json"), JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, "ready"), "ready");
        var stop = Path.Combine(dir, "stop");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!File.Exists(stop) && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        if (web is not null) await web.StopAsync();
        SpikeLog.Write("[pkgtest] done");
        return 0;
    }

    private static string Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { p.Kill(); return "timed out"; }
            return $"exit {p.ExitCode}: {output.Trim().Replace("\r", "").Replace("\n", " | ")}";
        }
        catch (Exception ex) { return "failed: " + ex.Message; }
    }
}
