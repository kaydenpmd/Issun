namespace Issun.Core.Presence;

/// <summary>
/// Makes a link fit Discord's 256-character limit without breaking it.
///
/// relay.py clipped every link at 256 characters, and so did Issun until
/// 23 Sept 2026. A clipped URL isn't a shorter link, it's a broken one: real
/// Apple Music links run past 256 when the album or song has a long name, and
/// cutting them off leaves a path that 404s — worse than no link at all.
///
/// Apple Music URLs carry the name only as decoration. The one that matters is
/// the numeric ID after it, and dropping the name segment opens the same page
/// (checked 23 Sept 2026: /us/album/eat-you-up/6778183551?i=6778183557 and
/// /us/album/6778183551?i=6778183557 are the same song page, while the clipped
/// form 404s). So a link that's too long loses its name segment first, and is
/// dropped only if it still doesn't fit.
/// </summary>
internal static class LinkFit
{
    /// <summary>The link as it can be sent, or null when no working form of it fits.</summary>
    public static string? For(string url, int max)
    {
        if (url.Length <= max)
            return url;

        var shortened = WithoutNameSegment(url);
        return shortened is not null && shortened.Length <= max ? shortened : null;
    }

    /// <summary>
    /// https://music.apple.com/us/album/some-long-name/123?i=456 becomes
    /// https://music.apple.com/us/album/123?i=456. Null for anything that isn't
    /// that shape, rather than guessing at a URL scheme nobody has checked.
    /// </summary>
    internal static string? WithoutNameSegment(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("music.apple.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        // country / kind / name / id — the id is all digits.
        if (segments.Length != 4 || segments[3].Length == 0 || !segments[3].All(char.IsAsciiDigit))
            return null;

        return $"{uri.Scheme}://{uri.Host}/{segments[0]}/{segments[1]}/{segments[3]}{uri.Query}";
    }
}
