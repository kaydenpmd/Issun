using System.Globalization;
using Issun.Core;

namespace Issun.Presentation;

/// <summary>
/// The settings the window lets a person edit, as the text they typed. Only
/// these fields: the key and the public address have their own controls in
/// Connect your source, and building a Settings from a stale copy of either
/// would quietly undo a change made there since the form loaded. (The public
/// address had a field here, under Advanced, until 12 Nov 2026: the owner had
/// it removed once the endpoint field did the same.)
/// </summary>
public sealed record SettingsFields
{
    public string DiscordClientId { get; init; } = "";
    public bool PublicRead { get; init; }
    public string Port { get; init; } = "8787";

    public static SettingsFields From(Settings s) => new()
    {
        DiscordClientId = s.DiscordClientId,
        PublicRead = s.PublicRead,
        Port = s.Port.ToString(CultureInfo.InvariantCulture),
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

        if (errors.Count > 0)
            return new(null, errors);

        return new(current with
        {
            DiscordClientId = clientId,
            PublicRead = f.PublicRead,
            Port = port,
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
