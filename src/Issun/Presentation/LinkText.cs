using System.Text.RegularExpressions;

namespace Issun.Presentation;

/// <summary>A run of command output, either plain text or a link that can be opened.</summary>
public sealed record TextPart(string Text, Uri? Link);

/// <summary>
/// Splits command output into text and links. `tailscale funnel` answers a
/// tailnet that hasn't allowed Funnel with a URL to the admin page that allows
/// it, and that link is the whole next step — so it has to be clickable rather
/// than something to copy out of a text box.
/// </summary>
public static partial class LinkText
{
    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    public static IReadOnlyList<TextPart> Split(string text)
    {
        var parts = new List<TextPart>();
        var at = 0;
        foreach (Match m in UrlPattern().Matches(text))
        {
            // Sentence punctuation after a URL belongs to the sentence.
            var url = m.Value.TrimEnd('.', ',', ';', ':', ')', ']');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                continue;
            if (m.Index > at)
                parts.Add(new(text[at..m.Index], null));
            parts.Add(new(url, uri));
            at = m.Index + url.Length;
        }
        if (at < text.Length)
            parts.Add(new(text[at..], null));
        return parts;
    }
}
