using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Issun.Core.Artwork;

/// <summary>
/// Cover art, catalog links and phone-uploaded JPEGs — relay.py's
/// artwork_by_store_id, catalog_links, store_uploaded_artwork,
/// existing_uploaded_artwork, artwork_url and the resolution order in
/// build_payload.
///
/// Resolution order, best first:
///   1. catalog ID from the phone (store_id ← playbackStoreID) — exact, no guessing
///   2. JPEG the phone uploaded with this push — exact, for tracks with no catalog ID
///   3. JPEG uploaded earlier in this track — the phone sends it once, not per heartbeat
///   4. fuzzy iTunes Search — best effort, and the source of most historical grief
///
/// For most of Ammy's life the phone sent neither the store ID nor the cover,
/// so 1–3 were dead code and every track went through 4: tracks missing from
/// the search index got no cover, and others got confidently wrong ones (a Wiz
/// Khalifa single matched to *Rolling Papers 2* at 0.48). With the ID present
/// the fuzzy path never runs, so a "weak match" line in the log now means the
/// phone didn't send a store ID for that track — a signal about the iOS side,
/// not about Apple's index.
///
/// Every answer is cached, negative ones included, so the presence worker can
/// ask every second without refetching or re-logging; concurrent callers for
/// the same key share one request. Every failure path logs: this project's
/// recurring bug is a function that returns nothing on failure and says
/// nothing, and an empty log that reads as "working".
///
/// The "[art] UNRESOLVED …" summary line is not written here. relay.py wrote it
/// in build_payload once every source had come up empty, and that half of
/// build_payload is <see cref="IActivityBuilder"/>'s; writing it here too would
/// print it twice.
/// </summary>
public sealed class ArtworkResolver : IArtworkResolver, IDisposable
{
    /// <summary>relay.py's urlopen(timeout=6), applied to the whole request.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long a lookup that *failed* — as opposed to one that answered "no" —
    /// is believed before it is tried again. relay.py cached failures for the
    /// life of the process, so one timeout or one iTunes rate-limit 403 at the
    /// start of a track cost that track its cover and its links until the relay
    /// was restarted. Issun sits in the tray for months, which would make that
    /// "forever".
    /// </summary>
    public static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(60);

    /// <summary>Accepted matches below this confidence are logged as weak.</summary>
    public const double WeakMatchBelow = 0.6;

    internal const int CacheCapacity = 4096;

    private const string LookupBase = "https://itunes.apple.com/lookup?";
    private const string SearchBase = "https://itunes.apple.com/search?";
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    private readonly IConfig _config;
    private readonly IClock _clock;
    private readonly HttpClient _http;
    private readonly UploadedArt _uploads;
    private readonly CancellationTokenSource _lifetime = new();

    // Everything below is guarded by _gate. Kestrel threads read the caches
    // (CachedArtwork, CachedLinks) while the presence worker fills them.
    private readonly object _gate = new();

    // _artwork_cache's "id:<store_id>" entries, together with _links_cache —
    // both come out of the same lookup response, so they live in one entry.
    private readonly BoundedMap<string, IdLookup> _byId = new(CacheCapacity);

    // _artwork_cache's "<artist>|<title>|<album>" entries.
    private readonly BoundedMap<string, SearchLookup> _bySearch = new(CacheCapacity);

    private readonly Dictionary<string, Task<IdLookup>> _idInFlight = new();
    private readonly Dictionary<string, Task<SearchLookup>> _searchInFlight = new();

    // Log-once bookkeeping for the uploaded-JPEG lines: "<what>|<track key>".
    private readonly BoundedMap<string, bool> _noted = new(CacheCapacity);

    public ArtworkResolver(IConfig config, string artDir, HttpMessageHandler? handler = null, IClock? clock = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? SystemClock.Instance;
        _uploads = new UploadedArt(artDir);
        _http = new HttpClient(
            handler ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = RequestTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            },
            disposeHandler: handler is null)
        {
            // The per-request limit is LookupTimeout, applied with a token so a
            // timeout reads as a timeout in the log rather than as a cancellation.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"Issun/{IssunInfo.Version}");
    }

    /// <summary>Where uploaded covers are kept.</summary>
    public string ArtDirectory => _uploads.Directory;

    /// <summary><see cref="RequestTimeout"/>, shortened by tests that exercise it.</summary>
    internal TimeSpan LookupTimeout { get; init; } = RequestTimeout;

    public async Task<ArtworkResult> ResolveAsync(TrackInfo track, CancellationToken ct)
    {
        if (track is null)
        {
            Log.Write("[art] asked to resolve artwork with no track");
            return ArtworkResult.None;
        }

        var title = "";
        try
        {
            title = TrackText.Title(track);
            var artist = TrackText.Artist(track);
            var album = TrackText.Album(track);
            var key = TrackText.Key(track);

            var storeId = TrackText.StoreId(track);
            if (storeId is not null)
            {
                var (url, matched) = await ArtworkByStoreIdAsync(storeId, ct).ConfigureAwait(false);
                if (url is not null)
                    return new ArtworkResult(url, matched, ArtworkSource.StoreId);
            }

            if (!string.IsNullOrEmpty(track.ArtworkB64) && StoreUploaded(key, title, artist, track.ArtworkB64) is { } stored)
                return new ArtworkResult(stored, album, ArtworkSource.Uploaded);

            // Heartbeats arrive without the JPEG; reuse the one already on disk.
            // Without this the cover would appear on the first push of a track
            // and vanish on the next one.
            if (ExistingUploaded(key, title, artist) is { } existing)
                return new ArtworkResult(existing, album, ArtworkSource.Uploaded);

            var (searched, searchedAlbum) = await ArtworkBySearchAsync(title, artist, album, ct).ConfigureAwait(false);
            return searched is not null
                ? new ArtworkResult(searched, searchedAlbum, ArtworkSource.Search)
                : ArtworkResult.None;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            // Shutting down, or the caller gave up waiting. Not a failure and not
            // an answer. A lookup already under way carries on and is cached for
            // the next caller: it belongs to the resolver, not to this call.
            return ArtworkResult.None;
        }
        catch (Exception ex)
        {
            Log.Write($"[art] could not resolve artwork for {title}: {Describe(ex)}");
            return ArtworkResult.None;
        }
    }

    /// <summary>
    /// relay.py's <c>artwork_by_store_id</c>: (cover, album it came from), or
    /// (null, "") — cached, negative answers included. Throws only
    /// OperationCanceledException, when <paramref name="ct"/> is cancelled.
    /// </summary>
    internal async Task<(string? Url, string MatchedAlbum)> ArtworkByStoreIdAsync(string storeId, CancellationToken ct)
    {
        var found = await LookupAsync(_byId, _idInFlight, storeId, () => FetchByIdAsync(storeId), ct)
            .ConfigureAwait(false);
        return found.Artwork is { } url ? (url, found.MatchedAlbum) : (null, "");
    }

    /// <summary>
    /// relay.py's <c>artwork_url</c>: fuzzy iTunes Search, judged against
    /// ART_MIN_SCORE as it stands now. (null, "") when nothing passes. Throws
    /// only OperationCanceledException, when <paramref name="ct"/> is cancelled.
    /// </summary>
    internal async Task<(string? Url, string MatchedAlbum)> ArtworkBySearchAsync(
        string title, string artist, string album, CancellationToken ct)
    {
        var key = $"{artist}|{title}|{album}";
        var found = await LookupAsync(_bySearch, _searchInFlight, key,
            () => FetchSearchAsync(title, artist, album), ct).ConfigureAwait(false);
        return Judge(found, title, artist) is { } accepted ? (accepted.Artwork, accepted.Album) : (null, "");
    }

    public CatalogLinks? CachedLinks(string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            return null;
        lock (_gate)
            return _byId.Peek(storeId.Trim(), out var hit) ? hit.Links : null;
    }

    public string? CachedArtwork(string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            return null;
        lock (_gate)
            return _byId.Peek(storeId.Trim(), out var hit) ? hit.Artwork : null;
    }

    public bool? CachedExplicit(string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            return null;
        lock (_gate)
            return _byId.Peek(storeId.Trim(), out var hit) ? hit.Explicit : null;
    }

    public string? UploadedArtPath(string name) => _uploads.PathFor(name);

    /// <summary>Abandons lookups in flight. The caches stay readable.</summary>
    public void Dispose()
    {
        // _lifetime itself is left undisposed: a lookup finishing on another
        // thread may still ask it whether it was cancelled, and a CTS with no
        // timer holds nothing that needs releasing.
        _lifetime.Cancel();
        _http.Dispose();
    }

    // ── Caching ─────────────────────────────────────────────────────────────

    private abstract class Lookup
    {
        /// <summary>When a failed lookup may be retried; null for an answer, which stands.</summary>
        public double? RetryAt { get; init; }

        public bool Stale(double now) => RetryAt is { } at && now >= at;
    }

    /// <summary>What one store-ID lookup found. <see cref="Links"/> and <see cref="Explicit"/> can be present without artwork.</summary>
    private sealed class IdLookup : Lookup
    {
        public string? Artwork { get; init; }
        public string MatchedAlbum { get; init; } = "";
        public CatalogLinks? Links { get; init; }
        public bool? Explicit { get; init; }
    }

    /// <summary>
    /// The best search result and its confidence — not the accept/reject
    /// decision, which is taken against ART_MIN_SCORE at the moment of asking so
    /// a changed setting applies to tracks already looked up.
    /// </summary>
    private sealed class SearchLookup : Lookup
    {
        public string? Artwork { get; init; }
        public string Album { get; init; } = "";
        public double Confidence { get; init; }

        /// <summary>
        /// The verdict last logged for this entry (guarded by _gate). relay.py
        /// logged once, at lookup time, because its threshold never changed while
        /// running; here it is logged once per verdict, so moving ART_MIN_SCORE
        /// across a cached track's confidence says so once and not every second.
        /// </summary>
        public bool? LoggedAccepted { get; set; }
    }

    private async Task<T> LookupAsync<T>(BoundedMap<string, T> cache, Dictionary<string, Task<T>> inFlight,
        string key, Func<Task<T>> fetch, CancellationToken ct) where T : Lookup
    {
        Task<T>? pending;
        TaskCompletionSource<T>? owner = null;
        lock (_gate)
        {
            if (cache.TryGet(key, out var hit) && !hit.Stale(_clock.Now))
                return hit;
            if (!inFlight.TryGetValue(key, out pending))
            {
                owner = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = owner.Task;
                inFlight[key] = pending;
            }
        }

        // The request belongs to the resolver, not to whichever caller happened
        // to start it: one caller cancelling must not cancel it for the others,
        // and its answer is worth caching either way.
        if (owner is not null)
            _ = CompleteAsync(owner, cache, inFlight, key, fetch);

        return await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task CompleteAsync<T>(TaskCompletionSource<T> owner, BoundedMap<string, T> cache,
        Dictionary<string, Task<T>> inFlight, string key, Func<Task<T>> fetch) where T : Lookup
    {
        try
        {
            var result = await fetch().ConfigureAwait(false);
            lock (_gate)
            {
                cache.Set(key, result);
                inFlight.Remove(key);
            }
            owner.SetResult(result);
        }
        catch (Exception ex)
        {
            // The fetchers catch everything themselves; this is only reachable
            // through a bug, and must still not leave the key stuck in flight.
            lock (_gate)
                inFlight.Remove(key);
            owner.SetException(ex);
        }
    }

    // ── 1. Store-ID lookup ──────────────────────────────────────────────────

    /// <summary>
    /// Exact lookup using the catalog ID the phone already knows. No fuzzy
    /// matching, no threshold — either the ID resolves or it doesn't.
    ///
    /// Links are taken only from here, never from fuzzy search: the search
    /// results carry the same fields, but a near-miss cover is a cosmetic
    /// annoyance while a link that opens the wrong song is a broken promise.
    /// The same response carries both, so links cost no extra request.
    /// </summary>
    private async Task<IdLookup> FetchByIdAsync(string storeId)
    {
        try
        {
            var url = LookupBase + PyUrl.UrlEncode([("id", storeId), ("entity", "song")]);
            using var payload = await GetJsonAsync(url).ConfigureAwait(false);
            var results = Results(payload.RootElement);
            if (results.Count == 0)
            {
                Log.Write($"[art] store id {storeId} not in catalog");
                return new IdLookup();
            }

            var entry = results[0];
            if (entry.ValueKind != JsonValueKind.Object)
                throw new LookupException("unexpected response: the first result is not an object");

            var links = new CatalogLinks(
                Text(entry, "trackViewUrl"),
                Text(entry, "artistViewUrl"),
                AlbumLink(storeId, Text(entry, "collectionViewUrl")));
            var isExplicit = Explicitness(storeId, entry);

            if (Text(entry, "artworkUrl100") is { } artwork)
            {
                return new IdLookup
                {
                    Artwork = Upscale(artwork),
                    MatchedAlbum = Text(entry, "collectionName") ?? "",
                    Links = NullIfEmpty(links),
                    Explicit = isExplicit,
                };
            }

            // Resolved, but the catalog entry carries no cover. Distinct from
            // "not in catalog" — links still work here.
            Log.Write($"[art] store id {storeId} resolved but has no artwork");
            return new IdLookup { Links = NullIfEmpty(links), Explicit = isExplicit };
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            Log.Write($"[art] id lookup failed for {storeId}: {Describe(ex)}");
            return new IdLookup { RetryAt = _clock.Now + FailureRetry.TotalSeconds };
        }
        catch (Exception)
        {
            // Disposed mid-request: not a failure worth a line, and not an answer.
            return new IdLookup { RetryAt = 0 };
        }
    }

    /// <summary>
    /// collectionViewUrl without its ?i=&lt;trackId&gt; (see <see cref="PyUrl.AlbumUrl"/>).
    /// relay.py ran this inside the lookup's try block, so a malformed URL
    /// failed the whole lookup and cost the track its cover and its other two
    /// links; here only the album link is lost.
    /// </summary>
    private static string? AlbumLink(string storeId, string? collectionViewUrl)
    {
        if (collectionViewUrl is null)
            return null;
        try
        {
            return PyUrl.AlbumUrl(collectionViewUrl) is { Length: > 0 } album ? album : null;
        }
        catch (FormatException ex)
        {
            Log.Write($"[art] store id {storeId}: album link dropped, collectionViewUrl is malformed ({ex.Message})");
            return null;
        }
    }

    private static CatalogLinks? NullIfEmpty(CatalogLinks links) =>
        links.Song is null && links.Artist is null && links.Album is null ? null : links;

    /// <summary>
    /// trackExplicitness as a flag. "explicit" is true; "cleaned" (the clean
    /// edit of an explicit song) and "notExplicit" are false. Anything else is
    /// no answer, and says so in the log: every song Apple has returned here
    /// carries one of those three words, so a missing field or a new word is
    /// the badge silently going away for every source that relies on this.
    /// Once per lookup, since the answer is cached. A missing field is logged
    /// only on an entry that says it is a song, the kind Apple defines the
    /// field for; the hand-made fixtures in the relay.py parity tests are
    /// nothing in particular, and relay.py logged nothing about them.
    /// </summary>
    private static bool? Explicitness(string storeId, JsonElement entry)
    {
        switch (Text(entry, "trackExplicitness"))
        {
            case null:
                if (Text(entry, "kind") == "song")
                    Log.Write($"[art] store id {storeId}: no trackExplicitness in the lookup, so not marked explicit");
                return null;
            case "explicit":
                return true;
            case "cleaned" or "notExplicit":
                return false;
            case var other:
                Log.Write($"[art] store id {storeId}: trackExplicitness \"{other}\" not recognised, so not marked explicit");
                return null;
        }
    }

    // ── 2 and 3. Uploaded JPEGs ─────────────────────────────────────────────

    /// <summary>
    /// Persist cover art sent by the phone and return a URL Discord can fetch.
    /// Used for tracks that aren't in the catalog at all — local files, iTunes
    /// Match uploads — where no lookup can possibly succeed.
    ///
    /// relay.py returned None without a word on every rejection here — no
    /// PUBLIC_BASE, bad base64, too big, not a JPEG — which is exactly the
    /// silent-failure pattern that made artwork bugs expensive. Each now logs,
    /// once per track: the push carrying the JPEG stays current for thirty
    /// seconds and this runs every one of them.
    /// </summary>
    private string? StoreUploaded(string key, string title, string artist, string b64)
    {
        var name = UploadedArt.FileName(key);
        // Already on disk: relay.py decoded and validated all ~80 KB again on
        // every call before finding that out. The outcome is the same URL.
        if (!_uploads.Exists(name))
        {
            switch (_uploads.Store(name, b64, out var detail))
            {
                case UploadedArt.Outcome.Rejected:
                    if (NoteOnce("upload-rejected", key))
                        Log.Write($"[art] uploaded cover for {title} — {artist} rejected: {detail}");
                    return null;
                case UploadedArt.Outcome.WriteFailed:
                    if (NoteOnce("upload-unsaved", key))
                        Log.Write($"[art] could not save uploaded cover for {title} — {artist}: {detail}");
                    return null;
            }
        }
        return PublicUrl(name, key, title, artist);
    }

    /// <summary>
    /// A cover the phone uploaded earlier in this track, if it's still on disk.
    /// The filename is a pure function of the track key, so no bookkeeping is
    /// needed to find it.
    /// </summary>
    private string? ExistingUploaded(string key, string title, string artist)
    {
        var name = UploadedArt.FileName(key);
        return _uploads.Exists(name) ? PublicUrl(name, key, title, artist) : null;
    }

    /// <summary>
    /// "{PUBLIC_BASE}/art/{name}", read per call. Discord's CDN fetches the
    /// image itself and can't reach 127.0.0.1, so without a public base there
    /// is no usable URL.
    ///
    /// Unlike relay.py, the JPEG has already been saved by the time this is
    /// asked: Issun learns its public address from Tailscale, possibly after
    /// the one push that carried the cover, and saving first means the cover
    /// appears as soon as the address is known instead of never.
    /// </summary>
    private string? PublicUrl(string name, string key, string title, string artist)
    {
        var publicBase = (_config.PublicBase ?? "").TrimEnd('/');
        if (publicBase.Length > 0)
            return $"{publicBase}/art/{name}";
        if (NoteOnce("upload-no-base", key))
        {
            Log.Write($"[art] uploaded cover for {title} — {artist} is saved but unused: no public address "
                + "for Discord to fetch it from (set PUBLIC_BASE, or turn on Tailscale Funnel)");
        }
        return null;
    }

    // ── 4. Fuzzy search ─────────────────────────────────────────────────────

    /// <summary>
    /// Public iTunes Search lookup, ranked. Takes the best candidate rather
    /// than demanding a close match — a near-miss cover beats a blank one,
    /// since the song and artist are shown as text regardless.
    /// </summary>
    private async Task<SearchLookup> FetchSearchAsync(string title, string artist, string album)
    {
        // Including the album narrows a huge number of near-duplicate releases.
        var term = string.Join(" ", new[] { artist, title, album }.Where(x => x.Length > 0));
        try
        {
            var url = SearchBase + PyUrl.UrlEncode([("term", term), ("entity", "song"), ("limit", "12")]);
            using var payload = await GetJsonAsync(url).ConfigureAwait(false);
            var results = Results(payload.RootElement)
                .Where(r => r.ValueKind == JsonValueKind.Object && Text(r, "artworkUrl100") is not null)
                .ToList();

            if (results.Count == 0)
            {
                // Zero usable results. Without this line the lookup returns
                // nothing having printed nothing, which reads as success and
                // hides the real cause: the track isn't in the iTunes Store
                // search index at all.
                Log.Write($"[art] no catalog results: {term}");
                return new SearchLookup();
            }

            // Rank with the album included, but score the winner both ways —
            // library and catalog album strings differ often enough that the
            // album shouldn't be able to veto an otherwise obvious match.
            // Strictly greater: Python's max() keeps the first of equal scores.
            var best = results[0];
            var bestScore = Score(best, title, artist, album);
            foreach (var candidate in results.Skip(1))
            {
                var score = Score(candidate, title, artist, album);
                if (score > bestScore)
                    (best, bestScore) = (candidate, score);
            }
            var confidence = Math.Max(bestScore, Score(best, title, artist, ""));

            return new SearchLookup
            {
                Artwork = Upscale(Text(best, "artworkUrl100")!),
                Album = Text(best, "collectionName") ?? "",
                Confidence = confidence,
            };
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            Log.Write($"[art] lookup failed for {title}: {Describe(ex)}");
            return new SearchLookup { RetryAt = _clock.Now + FailureRetry.TotalSeconds };
        }
        catch (Exception)
        {
            return new SearchLookup { RetryAt = 0 };
        }
    }

    /// <summary>
    /// ART_MIN_SCORE applied to a search result, read per call. Deliberately low
    /// (0.35): a near-miss cover still beats a blank one, and the floor only
    /// exists to catch tracks that aren't in the catalog, where every result is
    /// unrelated and showing one would be actively misleading.
    /// </summary>
    private (string Artwork, string Album)? Judge(SearchLookup found, string title, string artist)
    {
        if (found.Artwork is null)
            return null;

        var accepted = found.Confidence >= _config.Settings.ArtMinScore;
        bool announce;
        lock (_gate)
        {
            announce = found.LoggedAccepted != accepted;
            found.LoggedAccepted = accepted;
        }

        if (announce)
        {
            var confidence = PyFormat.Fixed2(found.Confidence);
            if (!accepted)
                Log.Write($"[art] nothing related ({confidence}): {title} — {artist}");
            else if (found.Confidence < WeakMatchBelow)
                Log.Write($"[art] weak match ({confidence}): {title} — {artist} → {found.Album}");
        }

        return accepted ? (found.Artwork, found.Album) : null;
    }

    private static double Score(JsonElement result, string title, string artist, string album) =>
        TitleMatch.Score(Text(result, "trackName"), Text(result, "artistName"), Text(result, "collectionName"),
            title, artist, album);

    // ── HTTP and JSON ───────────────────────────────────────────────────────

    private sealed class LookupException(string message) : Exception(message);

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Worded as urllib's HTTPError reads, so relay.log and Issun's
                // log say the same thing about the same failure.
                throw new LookupException(string.Create(CultureInfo.InvariantCulture,
                    $"HTTP Error {(int)response.StatusCode}: {response.ReasonPhrase}"));
            }
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            // json.load() accepts a UTF-8 byte-order mark; JsonDocument does not.
            ReadOnlyMemory<byte> json = body;
            if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                json = json[3..];
            return JsonDocument.Parse(json);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            throw new LookupException(string.Create(CultureInfo.InvariantCulture,
                $"timed out after {LookupTimeout.TotalSeconds:0.###}s"));
        }
    }

    /// <summary>
    /// <c>payload.get("results") or []</c>: a missing or falsy value is "no
    /// results"; any other shape relay.py would have crashed on is a failure.
    /// </summary>
    private static List<JsonElement> Results(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new LookupException("unexpected response: not a JSON object");
        if (!root.TryGetProperty("results", out var results) || !Truthy(results))
            return [];
        if (results.ValueKind != JsonValueKind.Array)
            throw new LookupException("unexpected response: results is not a list");
        return results.EnumerateArray().ToList();
    }

    /// <summary>Python's bool() over a JSON value.</summary>
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.String => value.GetString() is { Length: > 0 },
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.True => true,
        _ => false,
    };

    /// <summary>A non-empty string property, or null — Python's <c>entry.get(name) or ""</c> tested for truth.</summary>
    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>Apple serves any size from the same path; 512 is what Discord displays well.</summary>
    private static string Upscale(string artworkUrl100) => artworkUrl100.Replace("100x100bb", "512x512bb");

    private static string Describe(Exception ex) => ex switch
    {
        LookupException => ex.Message,
        HttpRequestException http => http.Message,
        JsonException json => $"invalid JSON ({json.Message})",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    /// <summary>True the first time <paramref name="what"/> is noted for this track.</summary>
    private bool NoteOnce(string what, string trackKey)
    {
        lock (_gate)
            return _noted.Add(what + "|" + trackKey, true);
    }
}
