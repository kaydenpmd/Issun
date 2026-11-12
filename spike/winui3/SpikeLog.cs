using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace IssunSpike;

/// <summary>
/// Everything the spike does, timestamped, appended to
/// %LOCALAPPDATA%\IssunSpike\spike.log (or ISSUN_SPIKE_LOG) from a background
/// thread that flushes every 250 ms, so a crash or a hang leaves the lines
/// before it. Each run starts with a header.
/// </summary>
public static class SpikeLog
{
    private static readonly BlockingCollection<string> Queue = new();
    private static string _path = "";

    public static string Path => _path;

    public static void Start(string[] args)
    {
        _path = Environment.GetEnvironmentVariable("ISSUN_SPIKE_LOG")
            ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IssunSpike", "spike.log");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var t = new Thread(Pump) { IsBackground = true, Name = "spike-log" };
        t.Start();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Write($"==== IssunSpike {version} pid {Environment.ProcessId} started {Process.GetCurrentProcess().StartTime:yyyy-MM-dd HH:mm:ss.fff} args: {string.Join(' ', args)}");
        Write($"[env] os {Environment.OSVersion.VersionString}, .NET {Environment.Version}, exe {Environment.ProcessPath}, base dir {AppContext.BaseDirectory}");
    }

    public static void Write(string message) =>
        Queue.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message);

    /// <summary>Waits until everything queued so far is on disk.</summary>
    public static void Flush()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Queue.Count > 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        Thread.Sleep(300);
    }

    private static void Pump()
    {
        using var file = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
        var last = DateTime.UtcNow;
        while (true)
        {
            if (Queue.TryTake(out var line, 250))
                file.WriteLine(line);
            if (Queue.Count == 0 || DateTime.UtcNow - last > TimeSpan.FromMilliseconds(250))
            {
                file.Flush();
                last = DateTime.UtcNow;
            }
        }
    }
}
