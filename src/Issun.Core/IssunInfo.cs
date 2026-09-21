using System.Reflection;

namespace Issun.Core;

/// <summary>
/// Which build this is. VersionPrefix in Directory.Build.props is the only
/// number a human sets; CI supplies the build number (a commit count) and the
/// short SHA. A local build reports build 0, shown as "dev".
/// </summary>
public static class IssunInfo
{
    private static readonly Assembly Assembly = typeof(IssunInfo).Assembly;

    public static string Version { get; } =
        (Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static int Build { get; } =
        int.TryParse(Metadata("IssunBuild"), out var n) ? n : 0;

    public static string Commit { get; } = Metadata("IssunCommit") ?? "local";

    /// <summary>"0.1.0 (12)", or "0.1.0 (dev)" — the same shape as Ammy's Version row, "1.0 (80)".</summary>
    public static string Display => Build > 0 ? $"{Version} ({Build})" : $"{Version} (dev)";

    /// <summary>
    /// Body of GET /version, value of the X-Ammy-Relay header, and the version
    /// half of every uptime-log stamp: "issun 0.1.0 (12)".
    /// </summary>
    public static string Wire => $"issun {Display}";

    private static string? Metadata(string key) =>
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}
