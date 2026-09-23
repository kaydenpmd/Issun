using Issun.Core;
using Issun.Demo;
using Issun.Startup;

namespace Issun;

/// <summary>
/// The one place the window learns which host it is driving. Everything else
/// in this project sees only <see cref="IIssunHost"/>.
/// </summary>
public static class HostFactory
{
    public static IIssunHost Create(string[] args)
    {
        var options = StartupOptions.Parse(args);

        // --demo, and --screenshot which implies it, must keep working after
        // integration: the demo is how the window gets looked at without
        // touching the port, Discord or the real settings.
        if (options.UsesDemoHost)
            return new DemoHost(startPhase: options.DemoPhase);

        return new Core.Host.IssunHost();
    }
}
