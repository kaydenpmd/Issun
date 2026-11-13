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
    /// What follows an explicit track's title on the card: a space and 🅴
    /// (U+1F174), typed rather than drawn, as iOS's Lock Screen appends its
    /// badge to the title as text (MRUNowPlayingInfo in iOS 26.1: the title
    /// <c>stringByAppendingFormat:@" %@"</c> the badge). In SF Pro that
    /// character is the SF Symbol e.square.fill, at cap height. Empty for
    /// any other track. The card sets this run's font to Segoe UI Symbol,
    /// whose 🅴 is also exactly cap height and takes the text's colour:
    /// left to fallback, WinUI drew Segoe UI Emoji's, a colour emoji that
    /// hangs below the baseline (the owner, 13 Nov 2026: "this explicit
    /// symbol looks like shit").
    /// </summary>
    public static string ExplicitBadge(bool isExplicit) => isExplicit ? " \U0001F174" : "";

    /// <summary>What the card says instead of a track, which depends on why there isn't one.</summary>
    public static string NothingPlayingDetail(HostSnapshot s) =>
        s.LastCheckinAt <= 0 ? "Nothing has checked in yet." :
        s.PhoneSilent ? "The source has gone quiet." :
        "The source is checking in, but nothing is playing.";

    /// <summary>
    /// "Source: Ammy 1.0 (97)" — the source's name and number, no "version"
    /// (the owner's wording, 23 Sept 2026). "Source: 1.0 (97)" from a source
    /// that sends no name; null before it has said which build it is. Only
    /// the log uses it now: the card dropped the line on 12 Nov 2026.
    /// </summary>
    public static string? SourceVersion(string? sourceName, string phoneVersion) =>
        string.IsNullOrWhiteSpace(phoneVersion) || phoneVersion == "unknown" ? null
        : string.IsNullOrWhiteSpace(sourceName) ? $"Source: {phoneVersion}"
        : $"Source: {sourceName} {phoneVersion}";
}
