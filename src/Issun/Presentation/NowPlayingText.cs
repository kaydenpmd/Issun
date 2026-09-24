using Issun.Core;

namespace Issun.Presentation;

/// <summary>The words on the now-playing card.</summary>
public static class NowPlayingText
{
    // Defaults as relay.py's build_payload applied them, via TrackText, so the
    // window never shows a title Discord wouldn't have been sent. Discord's
    // two-character padding (U+2060) is not applied here: it is invisible
    // anyway, and this card shows what the phone sent.
    public static string Title(TrackInfo t) => TrackText.Title(t);
    public static string Artist(TrackInfo t) => TrackText.Artist(t);

    /// <summary>
    /// The card's title: with 🅴 (U+1F174) after a space when the track is
    /// explicit, typed rather than drawn, the way Apple's Now Playing puts its
    /// E after the title in the title's own colour. WPF's font fallback draws
    /// it with Segoe UI Symbol (seen 24 Sept 2026). The tray keeps <see cref="Title"/>.
    /// </summary>
    public static string CardTitle(TrackInfo t, bool isExplicit) =>
        isExplicit ? $"{Title(t)} \U0001F174" : Title(t);

    /// <summary>What the card says instead of a track, which depends on why there isn't one.</summary>
    public static string NothingPlayingDetail(HostSnapshot s) =>
        s.LastCheckinAt <= 0 ? "Nothing has checked in yet." :
        s.PhoneSilent ? "The source has gone quiet." :
        "The source is checking in, but nothing is playing.";

    /// <summary>
    /// "Source: Ammy 1.0 (97)" — the source's name and number, no "version"
    /// (the owner's wording, 23 Sept 2026). "Source: 1.0 (97)" from a source
    /// that sends no name; null before it has said which build it is.
    /// </summary>
    public static string? SourceVersion(string? sourceName, string phoneVersion) =>
        string.IsNullOrWhiteSpace(phoneVersion) || phoneVersion == "unknown" ? null
        : string.IsNullOrWhiteSpace(sourceName) ? $"Source: {phoneVersion}"
        : $"Source: {sourceName} {phoneVersion}";

    /// <summary>Whether Discord is showing something, in a few words; null when there is nothing worth saying.</summary>
    public static string? OnDiscord(HostSnapshot s) =>
        s.Track is null ? null :
        s.Activity is not null ? "Showing on Discord" :
        s.Discord.State == DiscordLinkState.Connected ? "Sending to Discord…" :
        "Not on Discord";
}
