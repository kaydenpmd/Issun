using System.IO;
using System.Net.Http;
using Issun.Core;

namespace Issun.Artwork;

/// <summary>
/// Fetches cover art for the now-playing card, off the UI thread, and
/// remembers it. A WinUI image belongs to the thread that made it, so this
/// hands back the bytes, and the view model decodes them at the card's size.
///
/// The URL is whatever Discord was given: an Apple CDN address from the
/// store-ID lookup, or &lt;public base&gt;/art/&lt;hash&gt;.jpg for a JPEG the phone
/// uploaded. The phone sends that JPEG once per track and the heartbeat repeats
/// the same URL every 30 s, so without a cache the window would re-download the
/// same cover on every check-in.
///
/// A failed cover is logged once and then left alone for a while: the card
/// falls back to its placeholder, and retrying on every refresh would turn one
/// unreachable image into a line in the log four times a second.
/// </summary>
public sealed class CoverLoader
{
    private const int Capacity = 24;
    private static readonly TimeSpan RetryFailuresAfter = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = CreateHttp();

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();

    private sealed record Entry(Task<byte[]?> Load, LinkedListNode<string> Node)
    {
        public DateTime? FailedAt { get; set; }
    }

    /// <summary>The cover's bytes; null when it could not be had. Never throws.</summary>
    public Task<byte[]?> LoadAsync(string url)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var existing))
            {
                var stale = existing.FailedAt is { } failed && DateTime.UtcNow - failed > RetryFailuresAfter;
                if (!stale)
                {
                    _order.Remove(existing.Node);
                    _order.AddFirst(existing.Node);
                    return existing.Load;
                }
                _order.Remove(existing.Node);
                _entries.Remove(url);
            }

            var node = _order.AddFirst(url);
            var entry = new Entry(Task.Run(() => FetchAsync(url)), node);
            _entries[url] = entry;
            _ = entry.Load.ContinueWith(t =>
            {
                if (t.Result is null)
                    lock (_gate) entry.FailedAt = DateTime.UtcNow;
            }, TaskScheduler.Default);

            while (_order.Count > Capacity)
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _entries.Remove(oldest);
            }
            return entry.Load;
        }
    }

    private async Task<byte[]?> FetchAsync(string url)
    {
        try
        {
            return await ReadAsync(url);
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] cover art didn't load from {Describe(url)}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]?> ReadAsync(string url)
    {
        // Checked before Uri parsing, which refuses anything over 65,519
        // characters — and a base64 cover is several times that.
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // Only the demo produces these; the real host never does.
            var comma = url.IndexOf(',');
            if (comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                Log.Write("[ui] cover art data URL isn't base64");
                return null;
            }
            return Convert.FromBase64String(url[(comma + 1)..]);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Log.Write($"[ui] cover art URL isn't an http(s) address: {Describe(url)}");
            return null;
        }

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            Log.Write($"[ui] cover art didn't load from {Describe(url)}: HTTP {(int)response.StatusCode}");
            return null;
        }
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>The URL for a log line: never a data URL's payload, never a query string.</summary>
    private static string Describe(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return "an inline image";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : url.Length > 80 ? url[..80] + "…" : url;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Issun/{IssunInfo.Version}");
        return http;
    }
}
