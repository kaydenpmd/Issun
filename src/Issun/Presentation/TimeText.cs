using System.Globalization;
using Issun.Core;

namespace Issun.Presentation;

/// <summary>Durations and moments as the window writes them. Invariant culture throughout.</summary>
public static class TimeText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A playhead position: "3:07", or "1:02:45" past an hour. Negative and non-finite read as 0:00.</summary>
    public static string Clock(double seconds)
    {
        var total = double.IsFinite(seconds) && seconds > 0 ? (long)Math.Floor(seconds) : 0;
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0
            ? string.Format(Inv, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(Inv, "{0}:{1:00}", m, s);
    }

    /// <summary>"12s ago", "4m ago", "2h 5m ago". Seconds are kept up to 90 so a healthy 30 s heartbeat reads as seconds.</summary>
    public static string Ago(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 1)
            return "just now";
        var total = (long)Math.Floor(seconds);
        if (total < 90)
            return string.Format(Inv, "{0}s ago", total);
        if (total < 3600)
            return string.Format(Inv, "{0}m ago", total / 60);
        return string.Format(Inv, "{0}h {1}m ago", total / 3600, total % 3600 / 60);
    }

    /// <summary>A past moment in local time: "21:14" today, "20 Sep 21:14" otherwise.</summary>
    public static string Moment(double unixSeconds, DateTime nowLocal)
    {
        var at = UnixTime.ToLocal(unixSeconds);
        return at.Date == nowLocal.Date
            ? at.ToString("HH:mm", Inv)
            : at.ToString("d MMM HH:mm", Inv);
    }
}
