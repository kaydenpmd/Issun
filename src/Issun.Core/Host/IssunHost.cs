using Issun.Core.Artwork;
using Issun.Core.Discord;
using Issun.Core.Platform;
using Issun.Core.Presence;
using Issun.Core.Server;
using Issun.Core.State;

namespace Issun.Core.Host;

/// <summary>
/// The composition root: builds every module, wires them together, and gives
/// the window one object to talk to. What relay.py's main() did, plus the parts
/// relay.py left to a .env file, a Scheduled Task and a PowerShell window.
/// </summary>
public sealed class IssunHost : IIssunHost
{
    /// <summary>
    /// How often to try the port again while something else holds it. The usual
    /// culprit is relay.py still running from its Scheduled Task; retrying means
    /// stopping that task is enough, with no need to restart Issun afterwards.
    /// </summary>
    private static readonly TimeSpan PortRetryInterval = TimeSpan.FromSeconds(15);

    /// <summary>Tailscale's name and Funnel state rarely change; re-read them now and then anyway.</summary>
    private static readonly TimeSpan TailscaleInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Nudges the window even when nothing arrives, so "quiet since" and the
    /// idle timeout show up without waiting for an event that a silent phone
    /// will never send.
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly IClock _clock;
    private readonly SettingsStore _store;
    private readonly MutableConfig _config;
    private readonly PhoneState _state;
    private readonly PhoneDiagnostics _diag;
    private readonly UptimeLog _uptime;
    private readonly NotifyingCheckins _checkins;
    private readonly ArtworkResolver _art;
    private readonly ActivityBuilder _builder;
    private readonly PresenceWorker _worker;
    private readonly RelayServer _server;
    private readonly ITailscale _tailscale;
    private readonly IAutostart _autostart;
    private readonly SemaphoreSlim _serverGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private Task? _background;
    private TailscaleStatus? _tailscaleStatus;
    private bool _portFailureLogged;

    public event Action? Changed;

    public IssunHost(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;

        Directory.CreateDirectory(Paths.ConfigDir);
        Directory.CreateDirectory(Paths.DataDir);
        Directory.CreateDirectory(Paths.ArtCache);

        _store = new SettingsStore(Paths.SettingsFile);
        _config = new MutableConfig(_store.Current);

        _state = new PhoneState();
        _diag = new PhoneDiagnostics();
        _uptime = new UptimeLog(Paths.UptimeLog, _diag, _clock, _clock.Now);
        _checkins = new NotifyingCheckins(new CheckinProcessor(_state, _diag, _uptime, _clock), RaiseChanged);

        _art = new ArtworkResolver(_config, Paths.ArtCache);
        _builder = new ActivityBuilder(_config);
        _worker = new PresenceWorker(_config, () => new DiscordIpcClient(), _state, _builder, _art, _uptime, _clock);
        _server = new RelayServer(_config, _checkins, _state, _diag, _art, _clock);

        _tailscale = new TailscaleCli();
        _autostart = new Autostart();

        _worker.Changed += RaiseChanged;
        _server.Changed += RaiseChanged;
    }

    public HostSnapshot Snapshot
    {
        get
        {
            var now = _clock.Now;
            var (track, observedAt) = _state.Get();
            // The same rule the worker applies before building a payload, so the
            // window and Discord agree about when a track has gone stale.
            if (track is not null && now - observedAt > Timing.IdleTimeout)
                track = null;

            var last = _state.LastCheckinAt;
            return new HostSnapshot
            {
                Track = track,
                TrackObservedAt = observedAt,
                ArtworkUrl = track is null ? null : _worker.CurrentArtworkUrl,
                Activity = _worker.Current,
                LastCheckinAt = last,
                PhoneSilent = last > 0 && now - last > Timing.GapThreshold,
                PhoneVersion = _diag.PhoneVersion,
                DiagSummary = _diag.Latest is null ? "" : _diag.Summary(),
                Discord = _worker.Status,
                Server = _server.Status,
                Tailscale = _tailscaleStatus,
                PublicBase = _config.PublicBase,
            };
        }
    }

    public Settings Settings => _config.Settings;

    public bool FirstRun => _store.CreatedOnThisRun;

    public string DataFolder => Paths.DataDir;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
            throw new InvalidOperationException("Already started.");

        // The equivalent of relay.py's "[init] relay <version>": the first line
        // of every run says which build wrote the lines after it.
        Log.Write($"[init] issun {IssunInfo.Display} ({IssunInfo.Commit})");
        Log.Write($"[init] data in {Paths.DataDir}, settings in {Paths.ConfigDir}");
        if (string.IsNullOrEmpty(_config.Settings.DiscordClientId))
            Log.Write("[init] no Discord application ID yet; presence stays off until one is set");

        _uptime.Line("issun started");

        try { _autostart.RepairIfMoved(); }
        catch (Exception ex) { Log.Write($"[autostart] could not check the startup entry: {ex.Message}"); }

        // No installer, so the first run adds the Start menu entry one would have.
        new StartMenuShortcut().Ensure();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        await RefreshTailscaleAsync();
        await TryStartServerAsync(token);

        _workerTask = Task.Run(() => _worker.RunAsync(token), CancellationToken.None);
        _background = Task.Run(() => BackgroundAsync(token), CancellationToken.None);
    }

    public async Task UpdateSettingsAsync(Settings updated)
    {
        var problem = Validate(updated);
        if (problem is not null)
            throw new ArgumentException(problem);

        var previous = _config.Settings;
        _store.Save(updated);
        _config.Settings = updated;

        if (updated.Key != previous.Key)
            Log.Write($"[config] key changed ({updated.Key.Length} characters) — Ammy needs the new one");
        if (updated.DiscordClientId != previous.DiscordClientId)
            Log.Write("[config] Discord application ID changed");

        if (updated.Port != previous.Port && _cts is not null)
        {
            Log.Write($"[config] port {previous.Port} -> {updated.Port}; restarting the receiver");
            await _serverGate.WaitAsync();
            try { await _server.StopAsync(); }
            finally { _serverGate.Release(); }
            _portFailureLogged = false;
            await TryStartServerAsync(_cts.Token);
            // Funnel proxies to a port; if it still points at the old one, say so.
            await RefreshTailscaleAsync();
        }

        RaiseChanged();
    }

    public async Task<string> RegenerateKeyAsync()
    {
        var key = KeyGenerator.New();
        await UpdateSettingsAsync(_config.Settings with { Key = key });
        return key;
    }

    public async Task<EnvImportResult> ImportEnvAsync(string envPath)
    {
        // The importer logs what it took and every note itself.
        var result = EnvImporter.Import(envPath, _config.Settings, Paths.UptimeLog);
        await UpdateSettingsAsync(result.Settings);
        return result;
    }

    public async Task RefreshTailscaleAsync()
    {
        var port = _config.Settings.Port;
        TailscaleStatus status;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            status = await _tailscale.ProbeAsync(port, timeout.Token);
        }
        catch (Exception ex)
        {
            Log.Write($"[tailscale] probe failed: {ex.Message}");
            status = new TailscaleStatus(false, false, null, false, false, "Couldn't ask Tailscale for its status.");
        }

        // TailscaleCli logs whatever changed since its last probe, with the
        // tailnet masked, so nothing more is logged here.
        _tailscaleStatus = status;
        _config.DetectedPublicBase = status.PublicBase ?? "";
        RaiseChanged();
    }

    public async Task<(bool Ok, string Output)> EnableFunnelAsync()
    {
        Log.Write($"[tailscale] turning Funnel on for port {_config.Settings.Port} (requested from the window)");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = await _tailscale.EnableFunnelAsync(_config.Settings.Port, timeout.Token);
        Log.Write($"[tailscale] funnel command {(result.Ok ? "succeeded" : "failed")}: {result.Output.Trim()}");
        await RefreshTailscaleAsync();
        return result;
    }

    public bool StartWithWindows
    {
        get
        {
            try { return _autostart.IsEnabled; }
            catch (Exception ex)
            {
                Log.Write($"[autostart] could not read the startup entry: {ex.Message}");
                return false;
            }
        }
        set
        {
            if (value) _autostart.Enable();
            else _autostart.Disable();
            Log.Write($"[autostart] {(value ? "Issun will start with Windows" : "Issun will no longer start with Windows")}");
            RaiseChanged();
        }
    }

    public string UptimeSummary() => _uptime.Summarize();

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;

        _cts.Cancel();
        try
        {
            if (_workerTask is not null) await _workerTask;
            if (_background is not null) await _background;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"[init] shutdown error: {ex.Message}"); }

        await _server.DisposeAsync();
        // Owns an HttpClient; disposing cancels lookups still in flight.
        _art.Dispose();
        _cts.Dispose();
        _cts = null;
        Log.Write("[init] issun stopped");
    }

    private async Task BackgroundAsync(CancellationToken ct)
    {
        var nextTailscale = DateTime.UtcNow + TailscaleInterval;
        var nextPortTry = DateTime.UtcNow + PortRetryInterval;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);

                if (!_server.Status.Listening && DateTime.UtcNow >= nextPortTry)
                {
                    await TryStartServerAsync(ct);
                    nextPortTry = DateTime.UtcNow + PortRetryInterval;
                }

                if (DateTime.UtcNow >= nextTailscale)
                {
                    await RefreshTailscaleAsync();
                    nextTailscale = DateTime.UtcNow + TailscaleInterval;
                }

                RaiseChanged();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"[init] background error: {ex.Message}");
            }
        }
    }

    private async Task TryStartServerAsync(CancellationToken ct)
    {
        await _serverGate.WaitAsync(ct);
        try
        {
            if (_server.Status.Listening)
                return;
            await _server.StartAsync(_config.Settings.Port, ct);
            if (_portFailureLogged)
                Log.Write($"[http] port {_config.Settings.Port} came free; receiving again");
            _portFailureLogged = false;
        }
        catch (PortInUseException ex)
        {
            // The server logs each failure itself; this adds the one line that
            // says what happens next, once rather than every fifteen seconds.
            if (!_portFailureLogged)
                Log.Write($"[http] {ex.Message} Retrying every {PortRetryInterval.TotalSeconds:0}s.");
            _portFailureLogged = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"[http] receiver failed to start: {ex.Message}");
        }
        finally
        {
            _serverGate.Release();
            RaiseChanged();
        }
    }

    private static string? Validate(Settings s)
    {
        if (s.Port is < 1 or > 65535)
            return "Port must be between 1 and 65535.";
        if (s.Key.Length == 0)
            return "The key can't be empty — nothing could authenticate.";
        if (s.DiscordClientId.Length > 0 && !s.DiscordClientId.All(char.IsAsciiDigit))
            return "A Discord application ID is all digits.";
        if (s.StatusLine is not ("name" or "state" or "details"))
            return "Status line must be name, state or details.";
        if (s.PublicBase.Length > 0 && !(Uri.TryCreate(s.PublicBase, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
            return "The public base must be an https:// address.";
        if (s.ArtMinScore is < 0 or > 1 || double.IsNaN(s.ArtMinScore))
            return "The art match threshold is between 0 and 1.";
        return null;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Write($"[init] a Changed handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// Lets the window hear about a push the moment it lands, rather than on the
    /// next heartbeat. The processor itself has no event; this wraps it.
    /// </summary>
    private sealed class NotifyingCheckins(ICheckinProcessor inner, Action changed) : ICheckinProcessor
    {
        public CheckinResult Accept(NowPlayingPush push)
        {
            var result = inner.Accept(push);
            changed();
            return result;
        }
    }
}
