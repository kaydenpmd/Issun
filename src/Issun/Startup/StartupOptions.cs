using System.Globalization;

namespace Issun.Startup;

public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// The command line, parsed. Unknown arguments are reported rather than
/// ignored: a mistyped --backgorund in a Run key would otherwise open the
/// window at every logon with nothing saying why.
/// </summary>
public sealed record StartupOptions
{
    /// <summary>--demo: run on made-up data. Receives nothing, sends nothing, writes nothing to Issun's data folders.</summary>
    public bool Demo { get; init; }

    /// <summary>--background: start hidden in the tray. What the Start with Windows entry passes.</summary>
    public bool Background { get; init; }

    /// <summary>--screenshot &lt;png&gt;: render the window's content offscreen to this file and exit. Always on the demo host.</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>--theme light|dark: force a theme instead of following Windows. For screenshots of both.</summary>
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>--expanded: open every collapsed section before a screenshot.</summary>
    public bool Expanded { get; init; }

    /// <summary>--demo-phase N: start the demo's script at step N, to screenshot a particular state.</summary>
    public int DemoPhase { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>
    /// A screenshot never runs the real host: it must be safe to take while the
    /// real Issun is running, so it can't bind the port, open Discord's pipe or
    /// write settings.
    /// </summary>
    public bool UsesDemoHost => Demo || ScreenshotPath is not null;

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new StartupOptions();
        var problems = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next()
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return args[++i];
                problems.Add($"{arg} needs a value");
                return null;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--demo":
                    options = options with { Demo = true };
                    break;
                case "--background":
                    options = options with { Background = true };
                    break;
                case "--expanded":
                    options = options with { Expanded = true };
                    break;
                case "--screenshot":
                    if (Next() is { } png)
                        options = options with { ScreenshotPath = png };
                    break;
                case "--theme":
                    switch (Next()?.ToLowerInvariant())
                    {
                        case "light": options = options with { Theme = ThemeChoice.Light }; break;
                        case "dark": options = options with { Theme = ThemeChoice.Dark }; break;
                        case "system": options = options with { Theme = ThemeChoice.System }; break;
                        case null: break;
                        case var other: problems.Add($"--theme takes light, dark or system, not {other}"); break;
                    }
                    break;
                case "--demo-phase":
                    if (Next() is { } n)
                    {
                        if (int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var phase))
                            options = options with { DemoPhase = phase };
                        else
                            problems.Add($"--demo-phase takes a number, not {n}");
                    }
                    break;
                default:
                    problems.Add($"unknown argument {arg}");
                    break;
            }
        }

        return options with { Problems = problems };
    }
}
