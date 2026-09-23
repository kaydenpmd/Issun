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

    /// <summary>The album, or null when the phone sent none — an empty line would just be a gap.</summary>
    public static string? Album(TrackInfo t) => TrackText.Album(t) is { Length: > 0 } album ? album : null;

    /// <summary>What the card says instead of a track, which depends on why there isn't one.</summary>
    public static string NothingPlayingDetail(HostSnapshot s) =>
        s.LastCheckinAt <= 0 ? "Ammy hasn't checked in yet." :
        s.PhoneSilent ? "Ammy has gone quiet." :
        "Ammy is checking in, but nothing is playing.";

    /// <summary>"from Ammy 1.0 (80)", or null before the phone has said which build it is.</summary>
    public static string? FromAmmy(string phoneVersion) =>
        string.IsNullOrWhiteSpace(phoneVersion) || phoneVersion == "unknown" ? null : $"from Ammy {phoneVersion}";

    /// <summary>Whether Discord is showing something, in a few words; null when there is nothing worth saying.</summary>
    public static string? OnDiscord(HostSnapshot s) =>
        s.Track is null ? null :
        s.Activity is not null ? "Showing on Discord" :
        s.Discord.State == DiscordLinkState.Connected ? "Sending to Discord…" :
        "Not on Discord";
}
