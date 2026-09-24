using Issun.Core.Host;

namespace Issun.Core.Tests.Host;

/// <summary>
/// How soon the host looks at Tailscale again. At sign-in Issun can start
/// before Tailscale has connected, and on 24 Sept 2026 the window said "not
/// connected" for minutes after it had, because the next look was ten minutes
/// away.
/// </summary>
public class TailscaleScheduleTests
{
    private static TailscaleStatus Status(bool installed, bool running, bool funnelOn = false, bool targetsPort = false) =>
        new(installed, running, running ? "pc.example-tailnet.ts.net" : null, funnelOn, targetsPort, null);

    [Fact]
    public void Installed_but_not_connected_is_looked_at_again_soon()
    {
        Assert.Equal(IssunHost.TailscaleWaitingInterval, IssunHost.NextTailscaleProbe(Status(installed: true, running: false)));
        Assert.Equal(TimeSpan.FromSeconds(15), IssunHost.TailscaleWaitingInterval);
    }

    [Theory]
    [InlineData(true, true, true)]      // connected, Funnel serving this port
    [InlineData(true, true, false)]     // connected, Funnel left off on purpose: not probed every 15 s forever
    [InlineData(false, false, false)]   // not installed: nothing to wait for
    public void Anything_else_waits_the_usual_ten_minutes(bool installed, bool running, bool funnel)
    {
        Assert.Equal(TimeSpan.FromMinutes(10),
            IssunHost.NextTailscaleProbe(Status(installed, running, funnel, funnel)));
    }

    [Fact]
    public void Before_the_first_probe_the_usual_interval_applies()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), IssunHost.NextTailscaleProbe(null));
    }
}
