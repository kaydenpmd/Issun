using Issun.Core;

namespace Issun.Presentation;

/// <summary>The tray icon's tooltip: the track when there is one, otherwise the thing most worth knowing.</summary>
public static class TrayText
{
    /// <summary>
    /// NotifyIcon refuses text longer than 127 characters with an exception
    /// (the shell's own buffer is 128 including the terminator), and a long
    /// title plus artist gets there easily.
    /// </summary>
    public const int MaxLength = 127;

    public static string Tooltip(HostSnapshot s, DateTime nowLocal)
    {
        string text;
        if (s.Track is { } track)
            text = $"Issun – {NowPlayingText.Title(track)} · {NowPlayingText.Artist(track)}";
        else if (!s.Server.Listening)
            text = "Issun – not receiving: " + StatusText.Receiver(s.Server).Text;
        else if (s.LastCheckinAt <= 0)
            text = "Issun – waiting for the first push";
        else if (s.PhoneSilent)
            text = $"Issun – source quiet since {TimeText.Moment(s.LastCheckinAt, nowLocal)}";
        else if (s.Discord.State != DiscordLinkState.Connected)
            text = "Issun – Discord: " + StatusText.Discord(s.Discord).Text;
        else
            text = "Issun – not playing";
        return Clip(text);
    }

    public static string Clip(string text)
    {
        if (text.Length <= MaxLength)
            return text;
        var cut = MaxLength - 1;
        // Never split an emoji (a surrogate pair) in half: the shell would draw
        // the orphaned half as a box.
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut].TrimEnd() + "…";
    }
}
