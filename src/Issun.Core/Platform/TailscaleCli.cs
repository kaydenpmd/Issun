using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Issun.Core.Platform;

/// <summary>
/// Tailscale, read through its own command-line tool: is it installed, signed
/// in, what is this machine called, and is Funnel forwarding the public
/// address to Issun.
///
/// Funnel replaced a Cloudflare tunnel in September 2026 because it removed the
/// worst onboarding requirement — a domain and a Cloudflare account — and left
/// "a free Tailscale login" in its place. Every problem this class can find is
/// described in words the person can act on without knowing what a proxy
/// handler is, because each one is otherwise a phone that just says
/// "Connection Failed".
///
/// Only two things are ever run on a probe, both read-only:
/// <c>tailscale status --json</c> and <c>tailscale funnel status --json</c>.
/// The one command that changes anything is <see cref="EnableFunnelAsync"/>.
/// </summary>
public sealed partial class TailscaleCli : ITailscale
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longer than a probe: turning Funnel on for a tailnet that hasn't allowed
    /// it prints a login.tailscale.com link and then waits for someone to open
    /// it. Thirty seconds is enough to finish when the link isn't needed, and
    /// short enough to hand the link back when it is.
    /// </summary>
    public static readonly TimeSpan FunnelTimeout = TimeSpan.FromSeconds(30);

    private const string NotInstalledDetail =
        "Tailscale isn't installed. Install it from tailscale.com/download and sign in, then check again.";

    private readonly IProcessRunner _runner;
    private readonly Func<string?> _locate;
    private readonly object _logGate = new();
    private HashSet<string> _loggedLastProbe = new(StringComparer.Ordinal);

    /// <param name="runner">Stands in for real processes in tests.</param>
    /// <param name="locate">Finds tailscale.exe; null means <see cref="Locate()"/>.</param>
    public TailscaleCli(IProcessRunner? runner = null, Func<string?>? locate = null)
    {
        _runner = runner ?? ProcessRunner.Instance;
        _locate = locate ?? (() => Locate());
    }

    /// <summary>
    /// tailscale.exe on PATH, then in Program Files. Looked up on every call
    /// rather than once: installing Tailscale while Issun is running should be
    /// noticed on the next check, and a running process never sees the PATH the
    /// installer just changed — which is what the Program Files fallback is for.
    /// </summary>
    public static string? Locate() => Locate(
        Environment.GetEnvironmentVariable("PATH"),
        [Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)]);

    internal static string? Locate(string? pathVariable, IEnumerable<string?> programFilesDirs)
    {
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;
            try
            {
                var candidate = Path.Combine(trimmed, "tailscale.exe");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry. Windows skips those too.
            }
        }
        foreach (var programFiles in programFilesDirs)
        {
            if (string.IsNullOrWhiteSpace(programFiles))
                continue;
            var candidate = Path.Combine(programFiles, "Tailscale", "tailscale.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static string FunnelCommand(int port) =>
        $"tailscale funnel --bg --https=443 localhost:{port.ToString(CultureInfo.InvariantCulture)}";

    public async Task<TailscaleStatus> ProbeAsync(int port, CancellationToken ct)
    {
        var lines = new List<string>();
        var status = await ProbeCore(port, lines, ct).ConfigureAwait(false);
        if (status.Detail is { } detail)
            lines.Add($"[tailscale] {detail}");
        else
            lines.Add($"[tailscale] ready: Funnel publishes this PC's address to localhost:{Num(port)}");
        LogChanges(lines);
        return status;
    }

    /// <summary>
    /// Every line this class logs goes through here. The log gets pasted into
    /// chats and screenshots while debugging, so the tailnet half of any
    /// *.ts.net name is masked; the machine half stays, because "which PC" is
    /// often the question. What the person is shown on screen is never masked —
    /// only the log.
    /// </summary>
    internal static void Say(string line) => Log.Write(Redact(line));

    /// <summary>
    /// The label directly before ".ts.net" is always the tailnet, whether it
    /// arrives as part of a machine name or alone as the MagicDNS suffix.
    /// </summary>
    internal static string Redact(string text) => TailnetLabel().Replace(text, "<tailnet>");

    [GeneratedRegex(@"(?<![A-Za-z0-9-])[A-Za-z0-9-]+(?=\.ts\.net\b)", RegexOptions.IgnoreCase)]
    private static partial Regex TailnetLabel();

    /// <summary>
    /// The window re-probes on a timer, so a probe logs what changed since the
    /// previous one rather than repeating itself every few seconds: the first
    /// sighting of every failure is in the log, and so is its recovery. relay.py
    /// learned the same lesson in 1.3.1, when the padding notice went from once
    /// per second to once per value.
    /// </summary>
    private void LogChanges(List<string> lines)
    {
        lock (_logGate)
        {
            foreach (var line in lines)
            {
                if (!_loggedLastProbe.Contains(line))
                    Say(line);
            }
            _loggedLastProbe = new HashSet<string>(lines, StringComparer.Ordinal);
        }
    }

    private async Task<TailscaleStatus> ProbeCore(int port, List<string> log, CancellationToken ct)
    {
        var exe = _locate();
        if (exe is null)
        {
            log.Add("[tailscale] tailscale.exe not found on PATH or in Program Files");
            return new TailscaleStatus(false, false, null, false, false, NotInstalledDetail);
        }

        var result = await _runner.RunAsync(exe, ["status", "--json"], ProbeTimeout, ct).ConfigureAwait(false);
        if (Unanswered(result, "status --json", log) is { } unanswered)
            return new TailscaleStatus(true, false, null, false, false, unanswered);

        var self = ParseStatus(result.StdOut);
        if (self is null)
        {
            if (!result.Succeeded)
            {
                var said = FirstLine(result.StdErr, result.StdOut);
                log.Add($"[tailscale] status --json exited {Num(result.ExitCode ?? -1)}: {said}");
                return new TailscaleStatus(true, false, null, false, false, ServiceProblem(said));
            }
            log.Add($"[tailscale] status --json printed something that isn't Tailscale's status: {Clip(OneLine(result.StdOut))}");
            return new TailscaleStatus(true, false, null, false, false,
                "Tailscale answered with something Issun couldn't read. Updating Tailscale may fix it.");
        }

        if (self.BackendState != "Running")
        {
            log.Add($"[tailscale] backend state {self.BackendState}");
            return new TailscaleStatus(true, false, self.DnsName, false, false, DescribeBackendState(self.BackendState));
        }
        if (self.DnsName is null || self.MagicDnsEnabled == false)
        {
            log.Add("[tailscale] no MagicDNS name for this machine");
            return new TailscaleStatus(true, true, self.DnsName, false, false,
                "MagicDNS is off for your tailnet, and Funnel needs it. Turn it on under DNS in the Tailscale admin console, then check again.");
        }

        var funnel = await _runner.RunAsync(exe, ["funnel", "status", "--json"], ProbeTimeout, ct).ConfigureAwait(false);
        if (Unanswered(funnel, "funnel status --json", log) is { } funnelUnanswered)
            return new TailscaleStatus(true, true, self.DnsName, false, false, funnelUnanswered);
        if (!funnel.Succeeded)
        {
            var said = FirstLine(funnel.StdErr, funnel.StdOut);
            log.Add($"[tailscale] funnel status --json exited {Num(funnel.ExitCode ?? -1)}: {said}");
            return new TailscaleStatus(true, true, self.DnsName, false, false,
                $"Issun couldn't read Tailscale Funnel's settings. Tailscale said: {said}");
        }

        FunnelConfig config;
        try
        {
            config = FunnelConfig.Parse(funnel.StdOut);
        }
        catch (JsonException ex)
        {
            log.Add($"[tailscale] couldn't parse funnel status --json: {ex.Message}");
            return new TailscaleStatus(true, true, self.DnsName, false, false,
                "Tailscale answered with Funnel settings Issun couldn't read. Updating Tailscale may fix it.");
        }

        var (targets, detail) = Assess(config, self.DnsName, port, self.FunnelAllowed == false || self.HttpsAllowed == false);
        return new TailscaleStatus(true, true, self.DnsName, config.FunnelOn, targets, detail);
    }

    /// <summary>A command that never produced an answer at all: couldn't start, or ran out of time.</summary>
    private static string? Unanswered(ProcessResult result, string what, List<string> log)
    {
        if (!result.Started)
        {
            log.Add($"[tailscale] could not run tailscale {what}: {result.StartError}");
            return $"Issun couldn't run Tailscale ({result.StartError}). Reinstalling Tailscale may fix it.";
        }
        if (result.TimedOut)
        {
            log.Add($"[tailscale] {what} timed out after {Num(ProbeTimeout.TotalSeconds)} s");
            return "Tailscale didn't answer within 5 seconds. Quit Tailscale from its tray icon, start it again, then check again.";
        }
        return null;
    }

    private static string ServiceProblem(string said) =>
        said.Contains("failed to connect", StringComparison.OrdinalIgnoreCase)
        || said.Contains("not running", StringComparison.OrdinalIgnoreCase)
        || said.Contains("is Tailscale running", StringComparison.OrdinalIgnoreCase)
            ? "Tailscale is installed but its service isn't running. Start Tailscale from the Start menu, then check again."
            : $"Tailscale reported a problem: {said}";

    private static string DescribeBackendState(string state) => state switch
    {
        "NeedsLogin" => "Tailscale isn't signed in. Open Tailscale from the system tray and log in, then check again.",
        "NeedsMachineAuth" => "This PC is waiting to be approved in the Tailscale admin console (login.tailscale.com/admin/machines).",
        "Stopped" => "Tailscale is disconnected. Open it from the system tray and click Connect.",
        "Starting" => "Tailscale is still connecting. Give it a moment, then check again.",
        _ => $"Tailscale isn't connected (it reports \"{state}\"). Open it from the system tray and make sure it's connected.",
    };

    /// <summary>
    /// Whether Funnel gets the public address to Issun, and if not, what to say.
    /// A route only counts when it is the whole of https://&lt;this machine&gt;
    /// on 443, forwarding to plain HTTP on 127.0.0.1 or localhost at Issun's
    /// port — because that is exactly the address Issun hands Ammy
    /// (<see cref="TailscaleStatus.PublicBase"/> + "/now-playing"), and
    /// anything narrower leaves that address answering with someone else's 404.
    /// </summary>
    /// <param name="needsPermission">
    /// The tailnet hasn't granted Funnel or HTTPS certificates yet, so turning
    /// Funnel on will first print a link to allow it.
    /// </param>
    internal static (bool Targets, string? Detail) Assess(FunnelConfig config, string dnsName, int port, bool needsPermission = false)
    {
        var command = FunnelCommand(port);
        var ours = config.Routes.Where(r => r.HostMatches(dnsName) && r.Port == 443).ToList();
        var toIssun = config.Routes.Where(r => TargetsIssun(r.Proxy, port)).ToList();

        var whole = ours.Where(r => r.Mount == "/" && TargetsIssun(r.Proxy, port)).ToList();
        if (whole.Any(r => !r.Foreground))
            return (true, null);
        if (whole.Count > 0)
            return (true, "Funnel reaches Issun, but it was started in a terminal without --bg, so it stops when that "
                          + "window closes and won't come back after a restart. Turning Funnel on from Issun keeps it running.");

        if (ours.FirstOrDefault(r => TargetsIssun(r.Proxy, port)) is { } partial)
            return (false, $"Funnel sends only {partial.Mount} to Issun, but your source needs the whole address. "
                           + "Turning Funnel on from Issun fixes that.");
        if (toIssun.FirstOrDefault(r => r.Port != 443) is { } otherPort)
            return (false, $"Funnel publishes Issun on port {Num(otherPort.Port)}, but the address Issun gives your source "
                           + "assumes 443. Turning Funnel on from Issun adds 443.");
        if (ours.FirstOrDefault(r => r.Mount == "/") is { } elsewhere)
            return (false, $"Funnel is on, but it sends this PC's address to {Describe(elsewhere.Proxy)} rather than "
                           + $"Issun on localhost:{Num(port)}. Turning Funnel on from Issun replaces that.");
        if (config.FunnelOn)
            return (false, "Funnel is on for something else on this PC, but not for Issun. Turning Funnel on from Issun adds it.");

        return (false, "Funnel is off, so nothing outside your tailnet can reach Issun. Turn it on from Issun, "
                       + $"or run: {command}"
                       + (needsPermission
                           ? " — Tailscale will first give you a link to allow Funnel for your tailnet."
                           : ""));
    }

    /// <summary>
    /// http://localhost:{port} or http://127.0.0.1:{port}, with or without a
    /// trailing slash; the CLI stores "localhost:8787" as the former. Not
    /// [::1] — Issun binds 127.0.0.1 only, the way relay.py did — and not
    /// https+insecure://, because Issun speaks plain HTTP behind the tunnel.
    /// </summary>
    internal static bool TargetsIssun(string proxy, int port)
    {
        var text = proxy.Contains("://", StringComparison.Ordinal) ? proxy : "http://" + proxy;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttp
               && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1")
               && uri.Port == port
               && uri.AbsolutePath == "/"
               && uri.Query.Length == 0;
    }

    private static string Describe(string proxy)
    {
        var text = proxy.Contains("://", StringComparison.Ordinal) ? proxy : "http://" + proxy;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
            ? $"{uri.Host}:{Num(uri.Port)}"
            : proxy;
    }

    public async Task<(bool Ok, string Output)> EnableFunnelAsync(int port, CancellationToken ct)
    {
        if (port is < 1 or > 65535)
        {
            Say($"[tailscale] not turning Funnel on: {Num(port)} isn't a port");
            return (false, $"{Num(port)} isn't a usable port.");
        }
        var exe = _locate();
        if (exe is null)
        {
            Say("[tailscale] not turning Funnel on: tailscale.exe not found on PATH or in Program Files");
            return (false, NotInstalledDetail);
        }

        var command = FunnelCommand(port);
        string[] arguments = ["funnel", "--bg", "--https=443", $"localhost:{Num(port)}"];
        var result = await _runner.RunAsync(exe, arguments, FunnelTimeout, ct).ConfigureAwait(false);
        var output = Combine(result.StdOut, result.StdErr);

        if (!result.Started)
        {
            Say($"[tailscale] could not run {command}: {result.StartError}");
            return (false, $"Issun couldn't run Tailscale ({result.StartError}). Reinstalling Tailscale may fix it.");
        }
        if (result.TimedOut)
        {
            // Almost always the "Funnel is not enabled on your tailnet. To
            // enable, visit: https://login.tailscale.com/f/funnel?node=..."
            // prompt, which waits for the link to be opened. The output goes
            // back verbatim: that link is the next step, and only this output
            // carries it.
            Say($"[tailscale] {command} still waiting after {Num(FunnelTimeout.TotalSeconds)} s: {Clip(OneLine(output))}");
            var advice = "Tailscale was still waiting after 30 seconds. If it printed a link above, open it, "
                         + "allow Funnel, then turn Funnel on again.";
            return (false, output.Length > 0 ? output + Environment.NewLine + Environment.NewLine + advice : advice);
        }
        if (result.ExitCode != 0)
        {
            Say($"[tailscale] {command} exited {Num(result.ExitCode ?? -1)}: {Clip(OneLine(output))}");
            return (false, output.Length > 0
                ? output
                : $"Tailscale exited with code {Num(result.ExitCode ?? -1)} and said nothing.");
        }

        Say($"[tailscale] Funnel turned on: https 443 -> localhost:{Num(port)}");
        return (true, output);
    }

    private static string Combine(string stdout, string stderr)
    {
        var a = stdout.TrimEnd();
        var b = stderr.TrimEnd();
        return a.Length == 0 ? b : b.Length == 0 ? a : a + Environment.NewLine + b;
    }

    private static string FirstLine(params string[] texts)
    {
        foreach (var text in texts)
        {
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is not null)
                return Clip(line);
        }
        return "(nothing)";
    }

    private static string OneLine(string text) => NewLines().Replace(text.Trim(), " | ");

    private static string Clip(string text) => text.Length <= 400 ? text : text[..400] + "…";

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Num(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s*(\r\n|\r|\n)+\s*")]
    private static partial Regex NewLines();

    // ───────────────────────────── Parsing ─────────────────────────────

    /// <param name="BackendState">"Running", "NeedsLogin", "Stopped", "NeedsMachineAuth", "Starting", "NoState".</param>
    /// <param name="DnsName">Self.DNSName without its trailing dot; null when empty.</param>
    /// <param name="MagicDnsEnabled">CurrentTailnet.MagicDNSEnabled, when reported.</param>
    /// <param name="FunnelAllowed">Whether the node holds the "funnel" capability; null when not reported.</param>
    /// <param name="HttpsAllowed">Whether the tailnet has HTTPS certificates turned on ("https"); null when not reported.</param>
    internal sealed record SelfStatus(
        string BackendState, string? DnsName, bool? MagicDnsEnabled, bool? FunnelAllowed, bool? HttpsAllowed = null);

    /// <summary>Null when <paramref name="json"/> isn't a status object at all.</summary>
    internal static SelfStatus? ParseStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("BackendState", out var state)
                || state.ValueKind != JsonValueKind.String)
                return null;

            string? dns = null;
            bool? funnelAllowed = null;
            bool? httpsAllowed = null;
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                if (self.TryGetProperty("DNSName", out var name) && name.ValueKind == JsonValueKind.String)
                    dns = name.GetString()!.TrimEnd('.') is { Length: > 0 } trimmed ? trimmed : null;

                // CapMap is current; Capabilities is the deprecated list it
                // replaced (1.102 still sends both). Either names "funnel" when
                // the tailnet's policy grants it to this node, and "https" when
                // the tailnet has HTTPS certificates on — Funnel needs both, and
                // the CLI hands out a link to whichever is missing.
                string[]? names = null;
                if (self.TryGetProperty("CapMap", out var capMap) && capMap.ValueKind == JsonValueKind.Object)
                    names = capMap.EnumerateObject().Select(p => p.Name).ToArray();
                else if (self.TryGetProperty("Capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                    names = caps.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToArray();
                if (names is not null)
                {
                    funnelAllowed = names.Contains("funnel");
                    httpsAllowed = names.Contains("https");
                }
            }

            bool? magicDns = null;
            if (root.TryGetProperty("CurrentTailnet", out var tailnet) && tailnet.ValueKind == JsonValueKind.Object
                && tailnet.TryGetProperty("MagicDNSEnabled", out var enabled)
                && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                magicDns = enabled.GetBoolean();

            return new SelfStatus(state.GetString()!, dns, magicDns, funnelAllowed, httpsAllowed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <param name="HostPort">The Web key, e.g. "mypc.example-tailnet.ts.net:443".</param>
    /// <param name="Mount">The handler's path, "/" for the whole site.</param>
    /// <param name="Proxy">Where it forwards, as stored: "http://localhost:8787".</param>
    /// <param name="Foreground">Started without --bg: lives only as long as the terminal that started it.</param>
    internal sealed record FunnelRoute(string HostPort, string Mount, string Proxy, bool Foreground)
    {
        public string Host => HostPort.LastIndexOf(':') is var i and >= 0 ? HostPort[..i] : HostPort;

        public int Port => HostPort.LastIndexOf(':') is var i and >= 0
                           && int.TryParse(HostPort[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p)
            ? p
            : 443;

        public bool HostMatches(string dnsName) => Host.TrimEnd('.').Equals(dnsName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The parts of <c>tailscale funnel status --json</c> (a ServeConfig) that
    /// matter here. Web maps "host:port" to path handlers; AllowFunnel marks
    /// which of those hosts are public rather than tailnet-only; Foreground
    /// holds the same shape again for each session started without --bg.
    /// </summary>
    internal sealed class FunnelConfig
    {
        public List<FunnelRoute> Routes { get; } = [];

        /// <summary>Anything at all is published through Funnel.</summary>
        public bool FunnelOn { get; private set; }

        public static FunnelConfig Parse(string json)
        {
            var config = new FunnelConfig();
            if (string.IsNullOrWhiteSpace(json))
                return config;       // nothing served, nothing funnelled

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("expected a JSON object");
            config.Add(doc.RootElement, foreground: false);
            if (doc.RootElement.TryGetProperty("Foreground", out var sessions) && sessions.ValueKind == JsonValueKind.Object)
            {
                foreach (var session in sessions.EnumerateObject())
                {
                    if (session.Value.ValueKind == JsonValueKind.Object)
                        config.Add(session.Value, foreground: true);
                }
            }
            return config;
        }

        private void Add(JsonElement serve, bool foreground)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (serve.TryGetProperty("AllowFunnel", out var allow) && allow.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in allow.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.True)
                        allowed.Add(entry.Name);
                }
            }
            if (allowed.Count > 0)
                FunnelOn = true;

            if (!serve.TryGetProperty("Web", out var web) || web.ValueKind != JsonValueKind.Object)
                return;
            foreach (var site in web.EnumerateObject())
            {
                // A Web entry without AllowFunnel is `tailscale serve`: reachable
                // inside the tailnet, invisible to a phone on mobile data.
                if (!allowed.Contains(site.Name)
                    || site.Value.ValueKind != JsonValueKind.Object
                    || !site.Value.TryGetProperty("Handlers", out var handlers)
                    || handlers.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var handler in handlers.EnumerateObject())
                {
                    if (handler.Value.ValueKind == JsonValueKind.Object
                        && handler.Value.TryGetProperty("Proxy", out var proxy)
                        && proxy.ValueKind == JsonValueKind.String)
                        Routes.Add(new FunnelRoute(site.Name, handler.Name, proxy.GetString()!, foreground));
                }
            }
        }
    }
}
