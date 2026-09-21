using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace Issun.Core.Discord;

/// <summary>
/// Discord's local IPC channel — the sanctioned Rich Presence path, and the
/// reason Issun exists as a desktop program at all. The phone never talks to
/// Discord; setting presence from the phone would mean a Gateway connection
/// with a user token, which is self-botting and gets accounts terminated.
///
/// Replaces pypresence's Presence as relay.py used it. The wire format is the
/// one pypresence 4.6.2 produces (see <see cref="DiscordWire"/>); the reading
/// side is stricter than pypresence's, which took the first frame after a
/// request as its answer whatever it was.
///
/// One request at a time: a second caller waits for the first to finish, so
/// frames from two requests can never interleave on the pipe.
/// </summary>
public sealed class DiscordIpcClient : IDiscordClient
{
    public const string DefaultPipePrefix = "discord-ipc-";

    // pypresence's own exception texts. relay.log has "[rpc] Discord not
    // reachable (...)" and "[rpc] lost connection (...)" lines carrying them,
    // and a grep for any of these should find the Issun-era lines too. PipeClosed
    // is cut after its first sentence: the rest ("Catch this exception and
    // re-connect your instance.") was advice to the programmer, not news, and
    // "The pipe was closed" still matches both eras.
    internal const string NotFoundMessage = "Could not find Discord installed and running on this machine.";
    internal const string InvalidIdMessage = "Error Code: 4000 Message: Client ID is Invalid";
    internal const string NoResponseMessage = "No response was received from the pipe in time";
    internal const string PipeClosedMessage = "The pipe was closed.";

    private const int PipeCount = 10;

    // Discord's replies are a few kilobytes at most (READY is the largest). A
    // length past this means the stream is out of step, and believing it would
    // allocate whatever garbage the header claims.
    private const int MaxFrameBytes = 1 << 20;

    // What PeekNamedPipe reports once the other end has gone.
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = 232;
    private const int ErrorPipeNotConnected = 233;

    private static int _enumerationFailureLogged;

    private readonly string _pipePrefix;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _responseTimeout;
    private readonly SemaphoreSlim _io = new(1, 1);

    // Cancelled by DisposeAsync so a request in flight on another thread
    // unblocks instead of holding the pipe open.
    private readonly CancellationTokenSource _lifetime = new();

    private NamedPipeClientStream? _pipe;
    private string _clientId = "";
    private volatile bool _connected;
    private int _disposed;

    /// <param name="pipePrefix">Pipes \\.\pipe\{prefix}0..9 are tried in order. Tests pass their own prefix — never the real one.</param>
    /// <param name="connectTimeout">Per pipe. Default 1 s: a pipe that exists but will not accept is not worth waiting on.</param>
    /// <param name="responseTimeout">
    /// Per request, handshake included. Default 10 s, pypresence's
    /// response_timeout. pypresence applied none to the handshake, so a Discord
    /// that accepted the pipe and never answered hung relay.py's worker for good.
    /// </param>
    public DiscordIpcClient(string pipePrefix = DefaultPipePrefix, TimeSpan? connectTimeout = null, TimeSpan? responseTimeout = null)
    {
        _pipePrefix = pipePrefix;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(1);
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// True from a successful handshake until the pipe is known to be gone.
    ///
    /// pypresence only ever found out Discord had quit by failing the next
    /// write, so relay.py believed it was connected for as long as the phone
    /// had nothing new to send — a paused phone and a closed Discord could sit
    /// like that for hours. This asks the pipe itself, which costs one system
    /// call and no traffic, so the worker notices within a tick and the window
    /// stops saying "Connected" to a Discord that isn't there.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            if (!_connected || Volatile.Read(ref _disposed) != 0)
                return false;
            var pipe = _pipe;
            if (pipe is not null && PipeIsOpen(pipe))
                return true;

            // One-way: a closed pipe never reopens. Recording it also stops
            // DisposeAsync writing a CLOSE into a pipe with nobody on the end.
            _connected = false;
            return false;
        }
    }

    /// <summary>
    /// Opens the first of \\.\pipe\{prefix}0..9 that accepts, then handshakes.
    /// Throws <see cref="DiscordUnavailableException"/> when no pipe accepts or
    /// the one that did never finished the handshake, and
    /// <see cref="DiscordHandshakeException"/> when Discord refuses the client ID.
    /// </summary>
    public async Task<DiscordUser?> ConnectAsync(string clientId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected)
                throw new InvalidOperationException("Already connected to Discord.");

            var pipe = await OpenFirstPipeAsync(ct).ConfigureAwait(false);
            try
            {
                var hello = DiscordWire.Encode(DiscordWire.OpHandshake, DiscordWire.Hello(clientId));
                var user = await GuardAsync(async token =>
                {
                    await WriteFrameAsync(pipe, hello, token).ConfigureAwait(false);
                    return await ReadHandshakeReplyAsync(pipe, token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

                _pipe = pipe;
                _clientId = clientId;
                _connected = true;
                return user;
            }
            catch (DiscordConnectionLostException ex)
            {
                // The pipe opened and then closed or went quiet before READY.
                // pypresence called the first of those InvalidPipe ("Pipe Not
                // Found - Is Discord Running?") — it happens while Discord is
                // still starting up — so to the caller it is the same thing as
                // no pipe at all: not reachable yet, try again later.
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new DiscordUnavailableException($"Discord opened the pipe but didn't finish the handshake: {ex.Message}");
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// SET_ACTIVITY, then reads until the reply carrying this request's nonce.
    /// PINGs are answered along the way and unrelated frames skipped.
    ///
    /// A refusal (<c>evt: "ERROR"</c>) throws <see cref="DiscordRejectedException"/>
    /// and leaves the connection up — it is a verdict on this payload, not on
    /// the pipe. Everything else that goes wrong on the pipe throws
    /// <see cref="DiscordConnectionLostException"/> and closes it, because
    /// after a failed read nothing guarantees the next byte is a frame header.
    /// </summary>
    public async Task SetActivityAsync(DiscordActivity? activity, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Built before the pipe is touched, so that a problem with the payload
        // can never be mistaken for a problem with the connection — that
        // confusion, the other way round, is what the 1.3.0 fix was about.
        var nonce = Guid.NewGuid().ToString("N");
        var frame = DiscordWire.Encode(DiscordWire.OpFrame, DiscordWire.SetActivity(activity, Environment.ProcessId, nonce));

        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipe = _pipe;
            if (!_connected || pipe is null)
                throw new DiscordConnectionLostException("Not connected to Discord.");

            try
            {
                await GuardAsync(async token =>
                {
                    await WriteFrameAsync(pipe, frame, token).ConfigureAwait(false);
                    await ReadReplyAsync(pipe, nonce, token).ConfigureAwait(false);
                    return true;
                }, ct).ConfigureAwait(false);
            }
            catch (DiscordRejectedException)
            {
                // The one failure that leaves the stream in step: the whole
                // ERROR frame was read. Keep the connection.
                throw;
            }
            catch
            {
                await MarkBrokenAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// Sends CLOSE (opcode 2, the same body as the handshake — what pypresence's
    /// close() sent) and closes the pipe. Discord clears the activity when the
    /// connection goes, so this is also how presence is cleared on exit.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        var acquired = await _io.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        try
        {
            var pipe = _pipe;
            if (_connected && pipe is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    var close = DiscordWire.Encode(DiscordWire.OpClose, DiscordWire.Hello(_clientId));
                    await WriteFrameAsync(pipe, close, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Write($"[rpc] couldn't send Discord a CLOSE ({ex.Message}); closing the pipe anyway");
                }
            }

            _connected = false;
            _pipe = null;
            if (pipe is not null)
                await pipe.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
                _io.Release();
        }
    }

    private async Task<NamedPipeClientStream> OpenFirstPipeAsync(CancellationToken ct)
    {
        string? refusal = null;
        foreach (var name in CandidatePipeNames())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
                return pipe;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // A pipe that exists but will not take a connection — every
                // instance busy, or a Discord that is still starting. Try the
                // next, and say which one refused if none work.
                await pipe.DisposeAsync().ConfigureAwait(false);
                refusal ??= $"{name} did not accept a connection: {ex.Message}";
            }
        }

        throw new DiscordUnavailableException(refusal ?? NotFoundMessage);
    }

    /// <summary>
    /// The pipe names worth trying, in order. pypresence listed \\?\pipe\ too
    /// and only tried names that were there. Asking for a pipe that does not
    /// exist costs the full connect timeout in .NET — it keeps retrying in case
    /// the pipe appears — which with Discord closed would be ten seconds a
    /// retry, so absent names are skipped. If the listing itself fails, every
    /// name is tried rather than none.
    /// </summary>
    private IEnumerable<string> CandidatePipeNames()
    {
        HashSet<string>? present = null;
        try
        {
            present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith(_pipePrefix, StringComparison.OrdinalIgnoreCase))
                    present.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            present = null;
            if (Interlocked.Exchange(ref _enumerationFailureLogged, 1) == 0)
                Log.Write($"[rpc] couldn't list named pipes ({ex.Message}); trying each Discord pipe in turn");
        }

        for (var i = 0; i < PipeCount; i++)
        {
            var name = _pipePrefix + i.ToString(CultureInfo.InvariantCulture);
            if (present is null || present.Contains(name))
                yield return name;
        }
    }

    /// <summary>
    /// Waits for READY. A CLOSE here is Discord refusing the client ID — 4000
    /// "Invalid Client ID", which reads like a deleted application but has been
    /// a misread digit (Ammy's CLAUDE.md, "Don't transcribe config from
    /// screenshots"). pypresence turned that message into InvalidID with its own
    /// wording, reproduced here so the log line reads the same as it did.
    /// </summary>
    private static async Task<DiscordUser?> ReadHandshakeReplyAsync(Stream pipe, CancellationToken token)
    {
        while (true)
        {
            var (op, json) = await ReadFrameAsync(pipe, token).ConfigureAwait(false);
            switch (op)
            {
                case DiscordWire.OpPing:
                    await WriteFrameAsync(pipe, DiscordWire.Encode(DiscordWire.OpPong, json ?? new JsonObject()), token).ConfigureAwait(false);
                    continue;

                case DiscordWire.OpClose:
                    throw HandshakeRefused(json);

                case DiscordWire.OpFrame when json is not null:
                    // pypresence treated any reply carrying "code" as a refusal.
                    if (DiscordWire.Get(json, "code") is not null)
                        throw HandshakeRefused(json);
                    if (DiscordWire.Text(DiscordWire.Get(json, "evt")) == "ERROR")
                        throw HandshakeRefused(DiscordWire.Get(json, "data"));
                    if (DiscordWire.Text(DiscordWire.Get(json, "evt")) == "READY")
                        return ReadUser(DiscordWire.Get(DiscordWire.Get(json, "data"), "user"));
                    continue;

                default:
                    continue;
            }
        }
    }

    private static DiscordHandshakeException HandshakeRefused(JsonNode? body)
    {
        var code = DiscordWire.Int(DiscordWire.Get(body, "code"));
        var message = DiscordWire.Text(DiscordWire.Get(body, "message")) ?? "no reason given";
        if (message == "Invalid Client ID")
            return new DiscordHandshakeException(4000, InvalidIdMessage);
        // pypresence's DiscordError wording.
        return new DiscordHandshakeException(code, string.Create(CultureInfo.InvariantCulture, $"Error Code: {code} Message: {message}"));
    }

    private static DiscordUser? ReadUser(JsonNode? user)
    {
        if (user is not JsonObject)
            return null;
        return new DiscordUser(
            DiscordWire.Text(DiscordWire.Get(user, "id")) ?? "",
            DiscordWire.Text(DiscordWire.Get(user, "username")) ?? "",
            DiscordWire.Text(DiscordWire.Get(user, "global_name")));
    }

    private static async Task ReadReplyAsync(Stream pipe, string nonce, CancellationToken token)
    {
        while (true)
        {
            var (op, json) = await ReadFrameAsync(pipe, token).ConfigureAwait(false);
            switch (op)
            {
                case DiscordWire.OpPing:
                    await WriteFrameAsync(pipe, DiscordWire.Encode(DiscordWire.OpPong, json ?? new JsonObject()), token).ConfigureAwait(false);
                    continue;

                case DiscordWire.OpClose:
                {
                    var code = DiscordWire.Int(DiscordWire.Get(json, "code"));
                    var message = DiscordWire.Text(DiscordWire.Get(json, "message")) ?? "no reason given";
                    throw new DiscordConnectionLostException(
                        string.Create(CultureInfo.InvariantCulture, $"Discord closed the connection: {code} {message}"));
                }

                case DiscordWire.OpFrame when json is not null:
                {
                    var replyNonce = DiscordWire.Text(DiscordWire.Get(json, "nonce"));
                    var isError = DiscordWire.Text(DiscordWire.Get(json, "evt")) == "ERROR";

                    // An ERROR without a nonce can only be about the one request
                    // outstanding — there is never more than one.
                    if (replyNonce != nonce && !(isError && replyNonce is null))
                        continue;

                    if (isError)
                    {
                        // data.message verbatim. pypresence's ServerError stripped
                        // the brackets and capitalised it, which is why older
                        // relay.log lines read 'Child "activity" fails because
                        // child "details" fails ...'.
                        var data = DiscordWire.Get(json, "data");
                        throw new DiscordRejectedException(
                            DiscordWire.Int(DiscordWire.Get(data, "code")),
                            DiscordWire.Text(DiscordWire.Get(data, "message")) ?? "Discord refused the activity without a reason");
                    }
                    return;
                }

                default:
                    continue;
            }
        }
    }

    /// <summary>One frame: int32 LE opcode, int32 LE length, that many bytes of UTF-8 JSON.</summary>
    private static async Task<(int Op, JsonNode? Json)> ReadFrameAsync(Stream pipe, CancellationToken token)
    {
        var header = new byte[DiscordWire.HeaderBytes];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MaxFrameBytes)
            throw new DiscordConnectionLostException(
                string.Create(CultureInfo.InvariantCulture, $"Discord sent a frame claiming {length} bytes; the stream is out of step"));

        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, token).ConfigureAwait(false);
        if (length == 0)
            return (op, null);

        try
        {
            return (op, JsonNode.Parse(body));
        }
        catch (JsonException ex)
        {
            // The frame was read whole, so the stream is still in step; only
            // this frame is lost. Skipping it beats dropping the connection.
            Log.Write(string.Create(CultureInfo.InvariantCulture, $"[rpc] ignored an unreadable frame from Discord (opcode {op}: {ex.Message})"));
            return (op, null);
        }
    }

    private static async Task WriteFrameAsync(Stream pipe, byte[] frame, CancellationToken token)
    {
        await pipe.WriteAsync(frame, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one request under the response timeout and turns every way a pipe
    /// can fail into <see cref="DiscordConnectionLostException"/>. The caller's
    /// own cancellation passes through untouched: that is shutdown, not a fault.
    /// </summary>
    private async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        linked.CancelAfter(_responseTimeout);
        try
        {
            return await request(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DiscordConnectionLostException(
                _lifetime.IsCancellationRequested ? "The connection was closed." : NoResponseMessage);
        }
        catch (EndOfStreamException ex)
        {
            throw new DiscordConnectionLostException(PipeClosedMessage, ex);
        }
        catch (IOException ex)
        {
            throw new DiscordConnectionLostException($"The pipe broke: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            throw new DiscordConnectionLostException($"The pipe is unusable: {ex.Message}", ex);
        }
    }

    private async Task MarkBrokenAsync()
    {
        _connected = false;
        var pipe = _pipe;
        _pipe = null;
        if (pipe is not null)
            await pipe.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// False once Discord's end of the pipe has closed. Only the three errors
    /// that mean exactly that count; anything else reads as still open, because
    /// a false "closed" here would reconnect a healthy connection every tick.
    /// </summary>
    private static bool PipeIsOpen(PipeStream pipe)
    {
        try
        {
            var handle = pipe.SafePipeHandle;
            if (handle.IsClosed || handle.IsInvalid)
                return false;
            if (PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                return true;
            return Marshal.GetLastPInvokeError() is not (ErrorBrokenPipe or ErrorNoData or ErrorPipeNotConnected);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(
        SafePipeHandle pipe, IntPtr buffer, uint bufferSize, IntPtr bytesRead, IntPtr totalBytesAvailable, IntPtr bytesLeftThisMessage);
}
