using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Issun.Core.Artwork;

/// <summary>
/// Cover art the phone uploads for tracks that aren't in the catalog at all —
/// local files, iTunes Match uploads — where no lookup can possibly succeed.
/// Kept on disk and served at /art/&lt;name&gt;, because Discord's CDN fetches
/// the image itself and can't be handed bytes.
///
/// The filename is a pure function of the track (the first 20 hex characters
/// of SHA-256 over <see cref="TrackText.Key"/>), byte for byte what relay.py
/// computed. That is what lets a heartbeat that carries no JPEG find the one
/// uploaded earlier in the track without any bookkeeping, and what keeps an
/// art_cache carried over from relay.py usable as it is.
/// </summary>
internal sealed class UploadedArt
{
    /// <summary>ART_CACHE_LIMIT: files beyond this are pruned, oldest first.</summary>
    public const int CacheLimit = 60;

    /// <summary>ART_MAX_BYTES, of decoded JPEG.</summary>
    public const int MaxBytes = 3 * 1024 * 1024;

    // A crash between writing a temp file and renaming it leaves the temp
    // behind; anything this old is certainly abandoned.
    private static readonly TimeSpan AbandonedTempAge = TimeSpan.FromHours(1);

    private readonly object _gate = new();

    public UploadedArt(string artDir)
    {
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artDir));
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: store-ID and search artwork don't touch the disk. Every
            // later write will fail and say so too.
            Log.Write($"[art] cannot create art cache {Directory}: {ex.Message}");
        }
    }

    public string Directory { get; }

    /// <summary><c>hashlib.sha256(track_key.encode()).hexdigest()[:20] + ".jpg"</c>.</summary>
    public static string FileName(string trackKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trackKey)))[..20] + ".jpg";

    public bool Exists(string name) => File.Exists(Path.Combine(Directory, name));

    public enum Outcome { Stored, Rejected, WriteFailed }

    /// <summary>
    /// relay.py's store_uploaded_artwork minus the URL: strict base64, at most
    /// 3 MB, JPEG magic bytes, written only if absent, then pruned.
    /// <paramref name="detail"/> says why when the outcome isn't Stored.
    /// </summary>
    public Outcome Store(string name, string b64, out string? detail)
    {
        var blob = PyBase64.DecodeStrict(b64, out var error);
        if (blob is null)
        {
            detail = $"not valid base64 ({error})";
            return Outcome.Rejected;
        }
        if (blob.Length == 0)
        {
            detail = "it decoded to nothing";
            return Outcome.Rejected;
        }
        if (blob.Length > MaxBytes)
        {
            detail = $"{blob.Length.ToString(CultureInfo.InvariantCulture)} bytes is over the 3 MB limit";
            return Outcome.Rejected;
        }
        // Cheap sanity check: JPEG magic bytes. Avoids writing arbitrary uploads.
        if (blob.Length < 3 || blob[0] != 0xFF || blob[1] != 0xD8 || blob[2] != 0xFF)
        {
            detail = $"not a JPEG (starts {Convert.ToHexString(blob, 0, Math.Min(3, blob.Length))})";
            return Outcome.Rejected;
        }

        var path = Path.Combine(Directory, name);
        lock (_gate)
        {
            if (File.Exists(path))
            {
                detail = null;
                return Outcome.Stored;
            }

            // Written aside and renamed into place, so GET /art/ can never serve
            // half a file. relay.py wrote in place; Discord's CDN caches what it
            // fetches for a week, so a torn read would have stuck.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, blob);
                File.Move(temp, path, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                if (File.Exists(path))
                {
                    // Another writer got there first; the file is what we wanted.
                    detail = null;
                    return Outcome.Stored;
                }
                detail = ex.Message;
                return Outcome.WriteFailed;
            }

            Prune();
        }
        detail = null;
        return Outcome.Stored;
    }

    /// <summary>
    /// _prune_art_cache: keep the <see cref="CacheLimit"/> newest *.jpg by
    /// modification time. relay.py swallowed a failed delete without a word;
    /// that is the silent-failure pattern this project keeps paying for, so it
    /// logs here.
    /// </summary>
    public void Prune()
    {
        lock (_gate)
        {
            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo(Directory).EnumerateFiles()
                    .Where(f => f.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ThenBy(f => f.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"[art] could not list art cache {Directory} to prune it: {ex.Message}");
                return;
            }

            foreach (var stale in files.Take(Math.Max(0, files.Count - CacheLimit)))
            {
                try
                {
                    stale.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Write($"[art] could not prune {stale.Name}: {ex.Message}");
                }
            }

            try
            {
                foreach (var temp in new DirectoryInfo(Directory).EnumerateFiles("*.tmp"))
                {
                    if (DateTime.UtcNow - temp.LastWriteTimeUtc > AbandonedTempAge)
                        TryDelete(temp.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"[art] could not clear abandoned temp files in {Directory}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The file behind GET /art/&lt;name&gt;, or null. Only a bare name of
    /// letters, digits, "_", "-" and "." ending in ".jpg", directly inside the
    /// cache, that exists as a file.
    ///
    /// Stricter than relay.py, which took os.path.basename of whatever followed
    /// /art/ — so /art/anything/x.jpg served x.jpg. Every name this code writes
    /// is 20 hex digits and ".jpg", so nothing legitimate is refused; refusing
    /// separators, colons and leading dots outright means no Windows path
    /// quirk (alternate data streams, trailing-dot trimming, "..") has to be
    /// reasoned about.
    /// </summary>
    public string? PathFor(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name[0] == '.'
            || !name.EndsWith(".jpg", StringComparison.Ordinal))
            return null;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(Directory, name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!string.Equals(Path.GetDirectoryName(full), Directory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(full), name, StringComparison.Ordinal))
            return null;

        // relay.py resolved symlinks before checking the parent folder, so a
        // link planted in the cache couldn't point outside it. Nothing Issun
        // writes is a link, so refusing them outright is the same guarantee.
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"[art] could not delete {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
