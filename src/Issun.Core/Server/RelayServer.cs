using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Issun.Core.Server;

/// <summary>
/// The HTTP receiver: relay.py's <c>Handler</c> and <c>QuietHTTPServer</c>,
/// on Kestrel, listening on 127.0.0.1 only. Tailscale Funnel is the sole
/// ingress; nothing on the LAN can reach this directly.
///
/// Routes, as relay.py had them:
/// <code>
/// POST /now-playing   the phone writing            key
/// GET  /now-playing   anything else reading        key, unless PublicRead
/// GET  /health        exactly "ok"                 open — Shortcuts test for that string
/// GET  /version       IssunInfo.Wire               open
/// GET  /diag          the phone's last self-report key
/// GET  /status        alive / stale, for Shortcuts key
/// GET  /art/&lt;hash&gt;.jpg  phone-uploaded covers        open — Discord's CDN can't send the key
/// </code>
/// Same path, opposite directions, for /now-playing.
/// </summary>
public sealed class RelayServer : IRelayServer
{
    /// <summary>
    /// Stamped on every response so the phone can tell an answer from Issun
    /// apart from an answer from something in front of it. Status codes alone
    /// can't: Tailscale Funnel returns its own 404 when the hostname resolves
    /// but nothing is served on the port, which is the same 404 Issun sends for
    /// a wrong path. Ammy's PushOutcome reads the header's presence to say
    /// "Wrong Path" rather than "Nothing at That Address".
    ///
    /// relay.py set it only in _reply and _reply_json, so /art and its error
    /// pages went without; here it is on every response, errors included.
    /// </summary>
    public const string RelayHeader = "X-Ammy-Relay";

    private const string JpegCache = "public, max-age=604800";

    private readonly IConfig _config;
    private readonly ICheckinProcessor _checkins;
    private readonly IPhoneState _phone;
    private readonly IPhoneDiagnostics _diagnostics;
    private readonly IArtworkResolver _artwork;
    private readonly IClock _clock;
    private readonly RefusalLog _refusals;

    // Start and stop are serialised; requests never take this.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    private ServerStatus _status;
    private bool _changePending;

    public RelayServer(IConfig config, ICheckinProcessor checkins, IPhoneState phone,
        IPhoneDiagnostics diagnostics, IArtworkResolver artwork, IClock clock)
    {
        _config = config;
        _checkins = checkins;
        _phone = phone;
        _diagnostics = diagnostics;
        _artwork = artwork;
        _clock = clock;
        _refusals = new RefusalLog(clock);
        _status = new ServerStatus(false, config.Settings.Port, null);
    }

    public event Action? Changed;

    public ServerStatus Status => Volatile.Read(ref _status);

    // ───────────────────────────── Lifecycle ─────────────────────────────

    /// <summary>
    /// Binds 127.0.0.1:<paramref name="port"/>. Already listening means stop
    /// first, so a port change is one call. Port 0 takes any free port, and
    /// <see cref="Status"/> reports the one bound — tests use that.
    /// </summary>
    /// <exception cref="PortInUseException">Something else holds the port — usually relay.py, still running as the "Ammy Relay" task.</exception>
    public async Task StartAsync(int port, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);

        await _lifecycle.WaitAsync(ct);
        try
        {
            await StopCoreAsync();

            var app = Build(port);
            try
            {
                await app.StartAsync(ct);
            }
            catch (Exception ex)
            {
                await DisposeQuietlyAsync(app);

                if (IsAddressInUse(ex))
                {
                    var inUse = new PortInUseException(port, ex);
                    Log.Write(Invariant($"[http] can't listen on 127.0.0.1:{port}: {inUse.Message}"));
                    SetStatus(new ServerStatus(false, port, inUse.Message));
                    throw inUse;
                }

                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    Log.Write(Invariant($"[http] start on 127.0.0.1:{port} cancelled"));
                    SetStatus(new ServerStatus(false, port, null));
                    throw;
                }

                // Access denied rather than in use almost always means Windows has
                // reserved the port for Hyper-V, WSL or Docker. Nothing is
                // listening there, so "is relay.py still running?" would send
                // the owner looking for a process that doesn't exist.
                var reason = IsAccessDenied(ex)
                    ? Invariant($"Windows has reserved port {port} (see: netsh interface ipv4 show excludedportrange protocol=tcp) — choose another port")
                    : $"{ex.GetType().Name}: {ex.Message}";
                Log.Write(Invariant($"[http] can't listen on 127.0.0.1:{port}: {reason}"));
                SetStatus(new ServerStatus(false, port, reason));
                throw;
            }

            var bound = BoundPort(app) ?? port;
            _app = app;
            Log.Write(Invariant($"[http] listening on 127.0.0.1:{bound}"));
            SetStatus(new ServerStatus(true, bound, null));
        }
        finally
        {
            _lifecycle.Release();
            RaiseChangedIfPending();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycle.Release();
            RaiseChangedIfPending();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task StopCoreAsync()
    {
        if (_app is not { } app)
            return;
        _app = null;

        var port = Status.Port;
        try
        {
            await app.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Write(Invariant($"[http] error while stopping on 127.0.0.1:{port}: {ex.GetType().Name}: {ex.Message}"));
        }
        await DisposeQuietlyAsync(app);

        Log.Write(Invariant($"[http] stopped listening on 127.0.0.1:{port}"));
        SetStatus(new ServerStatus(false, port, null));
    }

    private WebApplication Build(int port)
    {
        // The empty builder rather than the slim one: no appsettings.json from
        // whatever the working directory happens to be, and no environment
        // variables. Either could otherwise add a "Kestrel:Endpoints" entry or
        // ASPNETCORE_URLS and bind something other than loopback — and this
        // must bind 127.0.0.1 and nothing else, whatever the machine has set.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.AddServerHeader = false;
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // A failed start is logged below in Issun's own words; the host's
        // "Hosting failed to start" would repeat it with a stack trace.
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.None);
        builder.Logging.AddProvider(new HttpLogProvider());

        builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

        var app = builder.Build();
        app.Run(HandleAsync);
        return app;
    }

    private static int? BoundPort(WebApplication app)
    {
        var addresses = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        var first = addresses?.FirstOrDefault();
        return first is not null && Uri.TryCreate(first, UriKind.Absolute, out var uri) ? uri.Port : null;
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsAddressInUse))
                return true;
        }
        return false;
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AccessDenied })
                return true;
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsAccessDenied))
                return true;
        }
        return false;
    }

    private static async Task DisposeQuietlyAsync(WebApplication app)
    {
        try
        {
            await app.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"[http] error disposing the server: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Called only under the lifecycle lock; the event is raised once it is released.</summary>
    private void SetStatus(ServerStatus status)
    {
        Volatile.Write(ref _status, status);
        Volatile.Write(ref _changePending, true);
    }

    /// <summary>
    /// Outside the lock, so a listener that reacts by starting or stopping the
    /// server can't deadlock against the start or stop that told it. A move
    /// from one port to another raises it once, with the final state, rather
    /// than once for "stopped" and again for "listening".
    /// </summary>
    private void RaiseChangedIfPending()
    {
        if (!Interlocked.Exchange(ref _changePending, false))
            return;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"[http] a status listener failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ───────────────────────────── Requests ──────────────────────────────

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var method = request.Method;

        // relay.py compared self.path.rstrip("/"): trailing slashes don't
        // matter, and "/" itself becomes "" and matches nothing. Comparisons
        // are ordinal, as Python's == was — "/Health" is a 404. Unlike
        // self.path, Request.Path has no query string, so "/now-playing?t=1"
        // — a web page's cache-buster — is still /now-playing.
        var path = (request.Path.Value ?? "").TrimEnd('/');

        context.Response.Headers[RelayHeader] = IssunInfo.Wire;

        try
        {
            // Methods are case-sensitive, as BaseHTTPRequestHandler's do_<METHOD>
            // lookup was; HttpMethods.IsGet would also accept "get".
            if (string.Equals(method, "POST", StringComparison.Ordinal))
                await PostAsync(context, path);
            else if (string.Equals(method, "GET", StringComparison.Ordinal))
                await GetAsync(context, path);
            else
            {
                // relay.py had no do_HEAD, do_PUT or do_OPTIONS, and
                // BaseHTTPRequestHandler answers those with 501.
                _refusals.Note("501", $"[http] 501 {RefusalLog.Printable(method, 16)} {RefusalLog.Printable(request.Path.Value)}: unsupported method");
                await ReplyAsync(context, 501, $"Unsupported method ('{method}')");
            }
        }
        catch (Exception ex) when (ClientWentAway(ex, context))
        {
            // Funnel (and cloudflared before it) severs its connection whenever
            // the tunnel restarts. That is not an error, and a stack trace at the
            // bottom of the log reads exactly like a crash.
            Log.Write("[http] client disconnected mid-request (normal on tunnel restart)");
        }
        catch (Exception ex)
        {
            // A real error keeps its full trace. relay.py's handle_error printed
            // one and then sent nothing; the phone gets a 500 here instead, which
            // Ammy shows as "Relay Error 500" rather than a timeout.
            Log.Write($"[http] {method} {RefusalLog.Printable(request.Path.Value)} failed: {ex}");
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.Headers[RelayHeader] = IssunInfo.Wire;
                await ReplyAsync(context, 500, "internal error");
            }
        }
    }

    private async Task PostAsync(HttpContext context, string path)
    {
        if (path != "/now-playing")
        {
            // Before the key check, as in relay.py: a wrong path is a 404 whoever asks.
            _refusals.Note("404 POST",
                $"[http] 404 POST {RefusalLog.Printable(context.Request.Path.Value)}: not found — Ammy's Endpoint should end in /now-playing");
            await ReplyAsync(context, 404, "not found");
            return;
        }

        // Checked before the body is read. A stranger never gets as far as
        // making this machine buffer what they sent.
        if (!await RefuseUnlessAuthorisedAsync(context, "POST /now-playing"))
            return;

        byte[] body;
        try
        {
            body = await ReadBodyAsync(context);
        }
        catch (BadHttpRequestException ex) when (!ClientWentAway(ex, context))
        {
            // Kestrel refusing the body itself: over its 30 MB limit (413),
            // arriving too slowly to be worth waiting for (408), or framed
            // wrongly, like a malformed chunk (400). All of them are the
            // sender's request being bad, not something here breaking, so they
            // get the status Kestrel chose and one line — not the catch-all's
            // 500 and stack trace, which at the bottom of the log reads like a
            // crash. relay.py never met these: it read exactly Content-Length
            // bytes and ignored chunked bodies entirely.
            var status = ex.StatusCode;
            _refusals.Note(Invariant($"{status} body"), Invariant($"[http] {status} POST /now-playing: {ex.Message}"));
            await ReplyAsync(context, status, status == StatusCodes.Status413PayloadTooLarge ? "too large" : "bad request");
            return;
        }

        var json = RequestJson.ParseObject(body, out var problem);
        NowPlayingPush? push = null;
        if (json is not null)
        {
            try
            {
                push = NowPlayingPush.Parse(json);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
            {
                // A backstop: RequestJson has already decoded every string, and
                // an oversized number converts to infinity as it did in Python.
                // If Parse still meets a value it can't convert, that is the
                // sender's malformed push, and it is answered as one.
                problem = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        if (push is null)
        {
            // relay.py answered this silently. It is the phone's own push
            // failing, and only an authenticated sender can reach this line.
            _refusals.Note("400 POST", $"[http] 400 POST /now-playing: bad json ({problem})");
            await ReplyAsync(context, 400, "bad json");
            return;
        }

        // Version, diag, seq ordering and gap logging all happen in there, atomically.
        _checkins.Accept(push);

        // Applied or dropped as out of order, the phone gets 204 either way,
        // as it did from relay.py: a stale push is not the phone's error.
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task GetAsync(HttpContext context, string path)
    {
        switch (path)
        {
            case "/health":
                // Body stays exactly "ok" — Shortcuts and the earlier setup
                // notes test for that string. The version has its own route.
                await ReplyAsync(context, 200, "ok");
                return;

            case "/version":
                await ReplyAsync(context, 200, IssunInfo.Wire);
                return;

            case "/diag":
                // The phone's last self-report, so the keepalive can be checked
                // without waiting for something to die. Keyed, because it
                // describes the device rather than the receiver.
                if (!await RefuseUnlessAuthorisedAsync(context, "GET /diag"))
                    return;
                if (_diagnostics.Latest is not { Count: > 0 })
                {
                    await ReplyAsync(context, 200, "no diagnostics yet");
                    return;
                }
                var age = (long)(_clock.Now - _diagnostics.LatestAt);
                await ReplyAsync(context, 200, Invariant($"{age}s ago: {_diagnostics.Summary()}"));
                return;

            case "/now-playing":
                // Reads what the phone last pushed: this is what lets anything
                // that isn't Discord consume the feed — a web page, an overlay,
                // a bot. PublicRead drops the key and adds CORS; a key can't be
                // the answer for a public page, which would have to embed it.
                var publicRead = _config.Settings.PublicRead;
                if (!publicRead && !await RefuseUnlessAuthorisedAsync(context, "GET /now-playing"))
                    return;
                await ReplyJsonAsync(context, NowPlayingProjection.Json(_phone, _artwork, _clock.Now), cors: publicRead);
                return;

            case "/status":
                // Lets a Shortcut check whether the phone is still reporting
                // before it bothers launching the app. iOS can't answer that
                // locally; this can, because the app checks in every 30 s.
                if (!await RefuseUnlessAuthorisedAsync(context, "GET /status"))
                    return;
                var last = _phone.LastCheckinAt;
                var fresh = last > 0 && _clock.Now - last < Timing.IdleTimeout;
                await ReplyAsync(context, 200, fresh ? "alive" : "stale");
                return;
        }

        if (path.StartsWith("/art/", StringComparison.Ordinal))
        {
            await ServeArtAsync(context, path);
            return;
        }

        _refusals.Note("404 GET", $"[http] 404 GET {RefusalLog.Printable(context.Request.Path.Value)}: not found");
        await ReplyAsync(context, 404, "not found");
    }

    /// <summary>
    /// Covers the phone uploaded for tracks with no catalog entry. Open,
    /// because Discord's CDN fetches the image itself and cannot send the key;
    /// the names are hashes, so they can't be enumerated.
    /// </summary>
    private async Task ServeArtAsync(HttpContext context, string path)
    {
        // os.path.basename, which on Windows splits on both separators: only
        // the last segment is ever looked up. The resolver then insists on a
        // .jpg directly inside the cache that exists.
        var rest = path["/art/".Length..];
        var name = rest[(rest.LastIndexOfAny(['/', '\\']) + 1)..];

        byte[]? blob = null;
        if (_artwork.UploadedArtPath(name) is { } file)
        {
            try
            {
                blob = await File.ReadAllBytesAsync(file, context.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Pruned between the check and the read, most likely. relay.py
                // raised here and dropped the connection with no response.
                Log.Write($"[http] couldn't read {RefusalLog.Printable(name)} from the art cache: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (blob is null)
        {
            _refusals.Note("404 art", $"[http] 404 GET {RefusalLog.Printable(context.Request.Path.Value)}: no such cover in the art cache");
            await ReplyAsync(context, 404, "not found");
            return;
        }

        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "image/jpeg";
        response.ContentLength = blob.Length;
        response.Headers.CacheControl = JpegCache;
        await response.Body.WriteAsync(blob, context.RequestAborted);
    }

    // ───────────────────────────── Helpers ───────────────────────────────

    /// <returns>True when the request may proceed; otherwise a 401 has been sent and logged.</returns>
    private async Task<bool> RefuseUnlessAuthorisedAsync(HttpContext context, string route)
    {
        // Read per request, never cached: a regenerated key applies to the next one.
        var verdict = KeyCheck.Check(context.Request.Headers, _config.Settings.Key);
        if (verdict == KeyVerdict.Accepted)
            return true;

        _refusals.Note(verdict == KeyVerdict.NoKeyConfigured ? "401 unset" : $"401 {route} {verdict}",
            $"[http] 401 {route}: {KeyCheck.Describe(verdict)}");
        await ReplyAsync(context, 401, "unauthorized");
        return false;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        return buffer.ToArray();
    }

    /// <summary>relay.py's _reply: text/plain with a Content-Length, no charset, UTF-8 bytes.</summary>
    private static Task ReplyAsync(HttpContext context, int status, string body) =>
        WriteAsync(context, status, "text/plain", body);

    /// <summary>relay.py's _reply_json, always 200 in practice.</summary>
    private static Task ReplyJsonAsync(HttpContext context, string json, bool cors)
    {
        var headers = context.Response.Headers;
        if (cors)
        {
            // Without this a browser on any other origin cannot read the body,
            // which is the entire point of PublicRead.
            headers.AccessControlAllowOrigin = "*";
        }
        headers.CacheControl = "no-store";
        return WriteAsync(context, 200, "application/json", json);
    }

    private static async Task WriteAsync(HttpContext context, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    /// <summary>
    /// relay.py's QuietHTTPServer.handle_error test, for Kestrel: the client
    /// went away, rather than something here going wrong.
    /// </summary>
    private static bool ClientWentAway(Exception ex, HttpContext context) =>
        context.RequestAborted.IsCancellationRequested
        || ex is ConnectionResetException or ConnectionAbortedException
        // Kestrel's name for a body that stopped short of its Content-Length:
        // the connection closed mid-upload.
        || (ex is BadHttpRequestException { StatusCode: StatusCodes.Status400BadRequest } bad
            && bad.Message.StartsWith("Unexpected end of request content", StringComparison.Ordinal));

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
