namespace Issun.Core;

/// <summary>
/// Where Issun keeps things. Never beside the .exe: a single-file download
/// lives wherever the browser put it, and moving it must not lose settings.
///
/// Settings roam with the profile (%APPDATA%\Issun). Logs, the uptime log and
/// the uploaded-artwork cache are machine-local (%LOCALAPPDATA%\Issun).
/// Setting ISSUN_HOME puts both under that one folder instead — for tests, and
/// for running a second copy without touching the real one.
/// </summary>
public static class Paths
{
    private static string? Home => Environment.GetEnvironmentVariable("ISSUN_HOME") is { Length: > 0 } h ? h : null;

    public static string ConfigDir =>
        Home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Issun");

    public static string DataDir =>
        Home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Issun");

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "issun.log");

    /// <summary>Same filename relay.py used, so an imported history continues in place.</summary>
    public static string UptimeLog => Path.Combine(DataDir, "ammy-uptime.log");

    public static string ArtCache => Path.Combine(DataDir, "art_cache");
}
