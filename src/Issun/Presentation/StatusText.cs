using System.Globalization;
using Issun.Core;

namespace Issun.Presentation;

public enum StatusLevel { Neutral, Good, Warning, Error }

public sealed record StatusLine(string Text, StatusLevel Level);

/// <summary>
/// The four status rows, as words. Each describes the present: a reason that
/// belonged to a past moment is only shown while it is still the current state,
/// which is the rule Ammy's own status row settled on (item 8 in Ammy's
/// CLAUDE.md) after a stale reason left its screen asserting something that had
/// stopped being true.
/// </summary>
public static class StatusText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static StatusLine Phone(HostSnapshot s, double now, DateTime nowLocal)
    {
        if (s.LastCheckinAt <= 0)
            return new("Waiting for the first push", StatusLevel.Neutral);

        // PhoneSilent is the host's verdict (quiet for longer than the 90 s gap
        // threshold), not recomputed here, so the window and the uptime log can
        // never disagree about whether the phone has gone quiet.
        if (s.PhoneSilent)
            return new($"Quiet since {TimeText.Moment(s.LastCheckinAt, nowLocal)}", StatusLevel.Warning);

        return new($"Checking in, last push {TimeText.Ago(now - s.LastCheckinAt)}", StatusLevel.Good);
    }

    public static StatusLine Discord(DiscordStatus d) => d.State switch
    {
        DiscordLinkState.Connected => new(
            d.User is { Length: > 0 } user ? $"Connected as {user}" : "Connected", StatusLevel.Good),
        DiscordLinkState.Connecting => new(Present(d.Detail) ?? "Connecting…", StatusLevel.Neutral),
        _ => new(Present(d.Detail) ?? "Not connected", StatusLevel.Warning),
    };

    public static StatusLine Receiver(ServerStatus s) => s.Listening
        ? new(string.Format(Inv, "Listening on port {0}", s.Port), StatusLevel.Good)
        : new(Present(s.Error) ?? string.Format(Inv, "Not listening on port {0}", s.Port), StatusLevel.Error);

    public static StatusLine Tailscale(TailscaleStatus? t, int port)
    {
        if (t is null)
            return new("Checking…", StatusLevel.Neutral);
        if (t.FunnelOn && t.FunnelTargetsPort)
        {
            // Reaching Issun with a caveat — Funnel started in a terminal
            // without --bg works now and stops with that terminal — is still a
            // warning, and the caveat is the thing to show.
            return Present(t.Detail) is { } caveat
                ? new(caveat, StatusLevel.Warning)
                : new(string.Format(Inv, "Funnel on → port {0}", port), StatusLevel.Good);
        }

        // The probe's own Detail says what to do next; these are only for a
        // probe that found something wrong and said nothing about it.
        var fallback =
            !t.Installed ? "Tailscale isn't installed" :
            !t.Running ? "Tailscale isn't running, or this PC isn't signed in" :
            !t.FunnelOn ? "Funnel is off, so nothing outside your tailnet can reach this PC" :
            string.Format(Inv, "Funnel is on, but not pointed at port {0}", port);
        return new(Present(t.Detail) ?? fallback, StatusLevel.Warning);
    }

    /// <summary>
    /// Whether to offer "Turn on Funnel": whenever Funnel isn't cleanly
    /// reaching Issun, which includes reaching it from a terminal that will
    /// close. Only when Tailscale is installed and signed in, though — before
    /// that the command can only fail, and the row already says what to do first.
    /// </summary>
    public static bool OfferFunnel(TailscaleStatus? t) =>
        t is { Installed: true, Running: true } && (!(t.FunnelOn && t.FunnelTargetsPort) || Present(t.Detail) is not null);

    private static string? Present(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
