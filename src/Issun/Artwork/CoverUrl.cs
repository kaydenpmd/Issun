using System.Globalization;

namespace Issun.Artwork;

/// <summary>Where the window should fetch a cover from, which is not always where Discord does.</summary>
public static class CoverUrl
{
    /// <summary>
    /// A cover the phone uploaded is served by Issun itself at
    /// &lt;public base&gt;/art/&lt;hash&gt;.jpg — an address meant for Discord's
    /// CDN, which cannot reach 127.0.0.1. The window is on this machine and
    /// can: fetching it through Tailscale Funnel would leave this PC, come back
    /// in through the tunnel, and fail whenever Funnel is down, which is
    /// exactly when the window is most likely being looked at. Every other
    /// address is returned unchanged.
    /// </summary>
    public static string ForWindow(string url, string publicBase, int port)
    {
        if (publicBase.Length == 0 || port is < 1 or > 65535)
            return url;
        var prefix = publicBase.TrimEnd('/') + "/art/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return url;
        return string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/art/{url[prefix.Length..]}");
    }
}
