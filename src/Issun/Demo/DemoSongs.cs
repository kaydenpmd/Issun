using System.IO;
using System.Text.Json;
using Issun.Core;

namespace Issun.Demo;

/// <summary>
/// Real songs for the demo, from a list kept only on this PC:
/// %LOCALAPPDATA%\Issun demo songs\songs.json, with each song's cover as a
/// JPEG in covers\ beside it. The list is picked from the owner's own
/// listening, and this repository is public, so it lives outside it (the
/// owner's choice, 12 Nov 2026: "On this PC only"). Without the folder, as on
/// any other PC, in the sandbox and in CI, the demo keeps its made-up songs.
///
/// songs.json is an array of { "title", "artist", "album", "duration"
/// (seconds), "storeId", "explicit", "cover" (a file name under covers\) }.
/// Covers become data URLs, like the made-up ones, so nothing is fetched.
/// </summary>
internal static class DemoSongs
{
    public sealed record Song(string Title, string Artist, string Album, double Duration, string? StoreId, bool Explicit, string? Cover);

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Issun demo songs");

    /// <summary>The list, shuffled; empty when there is none or it can't be read. Never throws.</summary>
    public static List<Song> LoadShuffled()
    {
        var path = Path.Combine(Folder, "songs.json");
        if (!File.Exists(path))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var songs = new List<Song>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var title = Text(e, "title");
                var artist = Text(e, "artist");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
                    continue;
                var duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 200;
                var isExplicit = e.TryGetProperty("explicit", out var x) && x.ValueKind == JsonValueKind.True;
                songs.Add(new Song(title, artist, Text(e, "album") ?? "", Math.Max(60, duration), Text(e, "storeId"),
                    isExplicit, Cover(Text(e, "cover"))));
            }
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(songs));
            Log.Write($"[demo] {songs.Count} songs from the list on this PC ({Folder})");
            return songs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log.Write($"[demo] couldn't read the song list in {Folder}, so the demo keeps its made-up songs: {ex.Message}");
            return [];
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The cover as a data URL, or null (the placeholder) when its file is missing.</summary>
    private static string? Cover(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return null;
        // Only a name under covers\, whatever the list says.
        var path = Path.Combine(Folder, "covers", Path.GetFileName(file));
        if (!File.Exists(path))
        {
            Log.Write($"[demo] no cover file {Path.GetFileName(file)} in the song list's covers folder");
            return null;
        }
        return "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
    }
}
