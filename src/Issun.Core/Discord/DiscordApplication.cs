namespace Issun.Core.Discord;

/// <summary>
/// The Discord application Issun connects as, the same for every install (the
/// owner, 14 Nov 2026: "embed application id and distribute it to everybody",
/// and "don't expose a field anywhere to change the application id"). It isn't
/// a secret: Discord hands it to anyone who looks at the activity. Dolphin and
/// PreMiD build theirs in the same way.
///
/// The application's own name only shows when a source sends no
/// <c>activity_name</c>: Issun passes that on as the activity's name, which
/// Discord shows after "Listening to" in its place (see ActivityBuilder).
/// </summary>
public static class DiscordApplication
{
    public const string Id = "1540121215140630598";
}
