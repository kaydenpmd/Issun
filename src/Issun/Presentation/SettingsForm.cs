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
    public bool PublicRead { get; init; }
    public string Port { get; init; } = "8787";
    public string PublicBase { get; init; } = "";

    public static SettingsFields From(Settings s) => new()
    {
        DiscordClientId = s.DiscordClientId,
        PublicRead = s.PublicRead,
        Port = s.Port.ToString(CultureInfo.InvariantCulture),
        PublicBase = s.PublicBase,
    };
}

public sealed record SettingsValidation(Settings? Settings, IReadOnlyList<string> Errors)
{
    public bool Ok => Settings is not null;
}

public static class SettingsForm
{
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

        if (errors.Count > 0)
            return new(null, errors);

        return new(current with
        {
            DiscordClientId = clientId,
            PublicRead = f.PublicRead,
            Port = port,
            PublicBase = publicBase,
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
