namespace Issun.Core;

/// <summary>
/// Everything relay.py read from its .env, as one record. Persisted as JSON at
/// <see cref="Paths.SettingsFile"/> by <see cref="ISettingsStore"/>.
/// </summary>
public sealed record Settings
{
    /// <summary>
    /// DISCORD_CLIENT_ID. The Discord application's *name* is the text after
    /// "Listening to", which is why the owner's application is called
    /// "Apple Music" — it describes the source, not the bridge.
    /// </summary>
    public string DiscordClientId { get; init; } = "";

    /// <summary>
    /// RELAY_KEY: must match Ammy's Key field. Generated on first run. An empty
    /// key refuses every authenticated request — a receiver with no key set
    /// should be unreachable, not reachable by anyone.
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>RELAY_PORT. Tailscale Funnel proxies to localhost on this port.</summary>
    public int Port { get; init; } = 8787;

    /// <summary>
    /// PUBLIC_BASE override, e.g. https://kaydenspc.example.ts.net, no trailing
    /// slash. Empty means derive it from Tailscale (see <see cref="IConfig.PublicBase"/>).
    /// Needed for phone-uploaded artwork: Discord's CDN fetches the image itself
    /// and cannot reach 127.0.0.1.
    /// </summary>
    public string PublicBase { get; init; } = "";

    /// <summary>STATUS_LINE: "name", "state" or "details" — which line the compact member list shows.</summary>
    public string StatusLine { get; init; } = "state";

    /// <summary>SHOW_ALBUM. Off by default: Discord renders large_text as both a tooltip and a visible third line.</summary>
    public bool ShowAlbum { get; init; }

    /// <summary>PUBLIC_READ: serve GET /now-playing with no key and with CORS. Publishes what you are listening to.</summary>
    public bool PublicRead { get; init; }

    /// <summary>ART_MIN_SCORE: fuzzy-match floor. Deliberately low; 0 always takes the best match.</summary>
    public double ArtMinScore { get; init; } = 0.35;
}

/// <summary>
/// Live configuration for components that run continuously. Read per use,
/// never cached: a regenerated key or a changed setting must apply to the next
/// request without a restart.
/// </summary>
public interface IConfig
{
    Settings Settings { get; }

    /// <summary>
    /// PUBLIC_BASE as the running components should use it: the Settings
    /// override if set, otherwise https://&lt;tailscale dns name&gt;, otherwise "".
    /// Never has a trailing slash.
    /// </summary>
    string PublicBase { get; }
}

/// <summary>An <see cref="IConfig"/> whose values are simply assigned. The host owns one; tests make their own.</summary>
public sealed class MutableConfig : IConfig
{
    private Settings _settings;
    private string _detectedBase = "";

    public MutableConfig(Settings? settings = null) => _settings = settings ?? new Settings();

    public Settings Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, value);
    }

    /// <summary>https://&lt;dns name&gt; from Tailscale, when known.</summary>
    public string DetectedPublicBase
    {
        get => Volatile.Read(ref _detectedBase);
        set => Volatile.Write(ref _detectedBase, (value ?? "").TrimEnd('/'));
    }

    public string PublicBase =>
        Settings.PublicBase is { Length: > 0 } explicitBase ? explicitBase.TrimEnd('/') : DetectedPublicBase;
}

public interface ISettingsStore
{
    Settings Current { get; }

    /// <summary>True when no settings file existed and this run created one (with a fresh key).</summary>
    bool CreatedOnThisRun { get; }

    /// <summary>Writes atomically (temp file + replace) and raises <see cref="Changed"/>.</summary>
    void Save(Settings settings);

    event Action<Settings>? Changed;
}
