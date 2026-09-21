using System.Net;
using System.Net.Sockets;
using System.Text;
using Issun.Core;
using Issun.Core.Server;

namespace Issun.Core.Tests.Server;

/// <summary>A real RelayServer on a free loopback port, with fakes behind it.</summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    public const string Key = "test-key-not-a-real-one";

    public MutableConfig Config { get; } = new(new Settings { Key = Key });
    public FakeCheckins Checkins { get; } = new();
    public FakePhoneState Phone { get; } = new();
    public FakeDiagnostics Diagnostics { get; } = new();
    public FakeArtwork Artwork { get; } = new();
    public ManualClock Clock { get; } = new(1_758_400_000.0);
    public RelayServer Server { get; }
    public HttpClient Http { get; private set; } = null!;

    public int Port => Server.Status.Port;

    private ServerHarness()
    {
        Server = new RelayServer(Config, Checkins, Phone, Diagnostics, Artwork, Clock);
    }

    /// <summary>Port 0: the OS picks a free one. Never 8787 — the live relay is there.</summary>
    public static async Task<ServerHarness> StartAsync()
    {
        var harness = new ServerHarness();
        await harness.Server.StartAsync(0, CancellationToken.None);
        harness.Http = NewClient(harness.Port);
        return harness;
    }

    public static HttpClient NewClient(int port) =>
        new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(10),
        };

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null,
        string? key = null, string? secret = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (key is not null)
            request.Headers.TryAddWithoutValidation("X-Relay-Key", key);
        if (secret is not null)
            request.Headers.TryAddWithoutValidation("X-Relay-Secret", secret);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? key = null) => SendAsync(HttpMethod.Get, path, key: key);

    public Task<HttpResponseMessage> PostAsync(string body, string? key = Key, string path = "/now-playing") =>
        SendAsync(HttpMethod.Post, path, body, key);

    /// <summary>
    /// Writes <paramref name="request"/> verbatim and reads until the server
    /// closes — for what HttpClient won't send (lowercase methods, bodies that
    /// stop short). Send "Connection: close" so the server does close.
    /// </summary>
    public async Task<string> RawAsync(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var buffer = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.CopyToAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Runs <paramref name="action"/> and waits for a matching log line from
    /// any thread — Kestrel's request threads are outside Log.Capture's reach.
    /// </summary>
    public static async Task<string> WaitForLogAsync(Func<string, bool> match, Func<Task> action)
    {
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(LogEntry entry)
        {
            if (match(entry.Text))
                seen.TrySetResult(entry.Text);
        }

        Log.Written += Handler;
        try
        {
            await action();
            return await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Log.Written -= Handler;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        await Server.DisposeAsync();
    }
}
