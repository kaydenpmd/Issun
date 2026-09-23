using System.Globalization;
using Issun.Core;

namespace Issun.Presentation;

/// <summary>
/// The settings the window lets a person edit, as the text they typed. Only
/// these fields: the key has its own controls, and building a Settings from a
/// stale copy of it would quietly undo a key regenerated since the form loaded.
/// </summary>
public sealed record SettingsFields
{
    public string DiscordClientId { get; init; } = "";
    public string StatusLine { get; init; } = "state";
    public bool ShowAlbum { get; init; }
    public bool PublicRead { get; init; }
    public string Port { get; init; } = "8787";
    public string PublicBase { get; init; } = "";
    public string ArtMinScore { get; init; } = "0.35";

    public static SettingsFields From(Settings s) => new()
    {
        DiscordClientId = s.DiscordClientId,
        StatusLine = s.StatusLine,
        ShowAlbum = s.ShowAlbum,
        PublicRead = s.PublicRead,
        Port = s.Port.ToString(CultureInfo.InvariantCulture),
        PublicBase = s.PublicBase,
        ArtMinScore = s.ArtMinScore.ToString(CultureInfo.InvariantCulture),
    };
}

public sealed record SettingsValidation(Settings? Settings, IReadOnlyList<string> Errors)
{
    public bool Ok => Settings is not null;
}

public static class SettingsForm
{
    /// <summary>relay.py's STATUS_LINE values, in the order the window lists them, with the words the window uses.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> StatusLines =
    [
        ("state", "Artist"),
        ("details", "Title"),
        ("name", "App name"),
    ];

    /// <summary>
    /// Checks the typed fields and, when all are valid, lays them over
    /// <paramref name="current"/> — the host's settings at the moment of
    /// applying, so everything the form doesn't edit is carried through as it
    /// is now rather than as it was when the form loaded.
    /// </summary>
    public static SettingsValidation Validate(SettingsFields f, Settings current)
    {
        var errors = new List<string>();

        // A Discord application ID is a snowflake: a 17–20 digit number. Empty is
        // allowed — the Discord row then says it needs one — but a typo is
        // caught here, because Discord's answer to one ("Error Code: 4000,
        // Client ID is Invalid") reads like a deleted application, and that
        // misreading has already cost the owner time once.
        var clientId = f.DiscordClientId.Trim();
        if (clientId.Length > 0 && (clientId.Length is < 17 or > 20 || !clientId.All(char.IsAsciiDigit)))
            errors.Add("The Discord application ID is the 17–20 digit number on the application's General Information page.");

        var statusLine = f.StatusLine.Trim().ToLowerInvariant();
        if (StatusLines.All(s => s.Value != statusLine))
            errors.Add("Pick what the member list shows: Artist, Title or App name.");

        if (!int.TryParse(f.Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            errors.Add("The port must be a whole number from 1 to 65535.");

        var publicBase = f.PublicBase.Trim().TrimEnd('/');
        if (publicBase.Length > 0 &&
            (!Uri.TryCreate(publicBase, UriKind.Absolute, out var uri)
             || uri.Scheme is not ("https" or "http")
             || uri.Host.Length == 0
             || uri.Query.Length > 0 || uri.Fragment.Length > 0))
        {
            errors.Add("The public address must be a full address like https://your-pc.example-tailnet.ts.net, or empty.");
        }

        // Invariant culture: "0.35" means the same thing on every machine. A
        // comma decimal is refused rather than guessed at.
        if (!double.TryParse(f.ArtMinScore.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture, out var score) || score is < 0 or > 1)
            errors.Add("The match threshold must be a number from 0 to 1, like 0.35.");

        if (errors.Count > 0)
            return new(null, errors);

        return new(current with
        {
            DiscordClientId = clientId,
            StatusLine = statusLine,
            ShowAlbum = f.ShowAlbum,
            PublicRead = f.PublicRead,
            Port = port,
            PublicBase = publicBase,
            ArtMinScore = score,
        }, errors);
    }

    /// <summary>Whether the typed fields describe something other than <paramref name="s"/>.</summary>
    public static bool Differs(SettingsFields f, Settings s)
    {
        var validated = Validate(f, s);
        // Anything that doesn't validate is an edit in progress by definition.
        return validated.Settings is not { } parsed || parsed != s;
    }
}
