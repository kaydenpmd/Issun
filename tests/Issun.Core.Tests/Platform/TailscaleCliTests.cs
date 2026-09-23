using Issun.Core.Platform;
using static Issun.Core.Tests.Platform.TailscaleFixtures;

namespace Issun.Core.Tests.Platform;

/// <summary>Stands in for tailscale.exe: answers by argument list and records every call.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult> _answers = new(StringComparer.Ordinal);

    public List<(string File, string[] Args, TimeSpan Timeout)> Calls { get; } = [];

    public FakeRunner Answer(string args, ProcessResult result)
    {
        _answers[args] = result;
        return this;
    }

    public FakeRunner Answer(string args, string stdout) => Answer(args, Ok(stdout));

    public static ProcessResult Ok(string stdout) => new(true, 0, stdout, "", false);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (Calls)
            Calls.Add((fileName, arguments.ToArray(), timeout));
        var key = string.Join(" ", arguments);
        return Task.FromResult(_answers.TryGetValue(key, out var result)
            ? result
            : throw new InvalidOperationException($"unexpected command: tailscale {key}"));
    }
}

public class TailscaleCliTests
{
    private const string Exe = @"C:\Program Files\Tailscale\tailscale.exe";
    private const string Status = "status --json";
    private const string FunnelStatus = "funnel status --json";

    private static TailscaleCli Cli(FakeRunner runner, string? exe = Exe) => new(runner, () => exe);

    private static FakeRunner Running(string funnel) => new FakeRunner().Answer(Status, StatusRunning).Answer(FunnelStatus, funnel);

    // ───────────── probing ─────────────

    [Fact]
    public async Task The_captured_setup_is_ready()
    {
        var runner = Running(FunnelToIssun);

        var status = await Cli(runner).ProbeAsync(8787, CancellationToken.None);

        Assert.Equal(new TailscaleStatus(true, true, DnsName, true, true, null), status);
        Assert.Equal("https://mypc.example-tailnet.ts.net", status.PublicBase);
    }

    [Fact]
    public async Task A_probe_runs_only_the_two_read_only_commands_with_a_five_second_limit()
    {
        var runner = Running(FunnelToIssun);

        await Cli(runner).ProbeAsync(8787, CancellationToken.None);

        Assert.Equal([Status, FunnelStatus], runner.Calls.Select(c => string.Join(" ", c.Args)));
        Assert.All(runner.Calls, c => Assert.Equal(Exe, c.File));
        Assert.All(runner.Calls, c => Assert.Equal(TimeSpan.FromSeconds(5), c.Timeout));
    }

    [Fact]
    public async Task Localhost_by_address_counts_too()
    {
        var status = await Cli(Running(FunnelToLoopbackIp)).ProbeAsync(8787, CancellationToken.None);
        Assert.True(status.FunnelTargetsPort);
        Assert.Null(status.Detail);
    }

    [Fact]
    public async Task Not_installed_runs_nothing_and_says_where_to_get_it()
    {
        var runner = new FakeRunner();
        using var log = Log.Capture();

        var status = await Cli(runner, exe: null).ProbeAsync(8787, CancellationToken.None);

        Assert.False(status.Installed);
        Assert.False(status.Running);
        Assert.Null(status.PublicBase);
        Assert.Contains("tailscale.com/download", status.Detail);
        Assert.Empty(runner.Calls);
        Assert.Contains("[tailscale] tailscale.exe not found on PATH or in Program Files", log.Lines);
    }

    [Fact]
    public async Task A_stopped_service_is_named_as_such()
    {
        var runner = new FakeRunner().Answer(Status, new ProcessResult(true, 1, "", DaemonNotRunning, false));
        using var log = Log.Capture();

        var status = await Cli(runner).ProbeAsync(8787, CancellationToken.None);

        Assert.True(status.Installed);
        Assert.False(status.Running);
        Assert.Equal("Tailscale is installed but its service isn't running. Start Tailscale from the Start menu, then check again.", status.Detail);
        Assert.Contains(log.Lines, l => l.StartsWith("[tailscale] status --json exited 1: failed to connect to local Tailscale daemon", StringComparison.Ordinal));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Signed_out_says_to_sign_in_and_does_not_ask_about_funnel()
    {
        var runner = new FakeRunner().Answer(Status, StatusNeedsLogin);

        var status = await Cli(runner).ProbeAsync(8787, CancellationToken.None);

        Assert.Equal(new TailscaleStatus(true, false, null, false, false,
            "Tailscale isn't signed in. Open Tailscale from the system tray and log in, then check again."), status);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Disconnected_says_to_connect()
    {
        var status = await Cli(new FakeRunner().Answer(Status, StatusStopped)).ProbeAsync(8787, CancellationToken.None);

        Assert.False(status.Running);
        Assert.Equal("Tailscale is disconnected. Open it from the system tray and click Connect.", status.Detail);
    }

    [Fact]
    public async Task MagicDns_off_is_pointed_out()
    {
        var status = await Cli(new FakeRunner().Answer(Status, StatusMagicDnsOff)).ProbeAsync(8787, CancellationToken.None);

        Assert.True(status.Running);
        Assert.False(status.FunnelOn);
        Assert.StartsWith("MagicDNS is off for your tailnet", status.Detail);
    }

    [Fact]
    public async Task Funnel_off_gives_the_command_to_run()
    {
        var status = await Cli(Running(FunnelOff)).ProbeAsync(8787, CancellationToken.None);

        Assert.Equal(new TailscaleStatus(true, true, DnsName, false, false,
            "Funnel is off, so nothing outside your tailnet can reach Issun. Turn it on from Issun, "
            + "or run: tailscale funnel --bg --https=443 localhost:8787"), status);
    }

    [Fact]
    public async Task Funnel_off_on_a_tailnet_that_has_not_allowed_it_warns_about_the_link()
    {
        var runner = new FakeRunner().Answer(Status, StatusRunningWithoutFunnelCaps).Answer(FunnelStatus, FunnelOff);

        var status = await Cli(runner).ProbeAsync(8787, CancellationToken.None);

        Assert.EndsWith("Tailscale will first give you a link to allow Funnel for your tailnet.", status.Detail);
    }

    [Fact]
    public async Task Funnel_pointed_at_another_port_says_which()
    {
        var status = await Cli(Running(FunnelToOtherPort)).ProbeAsync(8787, CancellationToken.None);

        Assert.True(status.FunnelOn);
        Assert.False(status.FunnelTargetsPort);
        Assert.Equal("Funnel is on, but it sends this PC's address to 127.0.0.1:3000 rather than Issun on localhost:8787. "
                     + "Turning Funnel on from Issun replaces that.", status.Detail);
    }

    [Fact]
    public async Task Issun_on_a_new_port_is_not_reached_by_the_old_funnel()
    {
        var status = await Cli(Running(FunnelToIssun)).ProbeAsync(9000, CancellationToken.None);

        Assert.True(status.FunnelOn);
        Assert.False(status.FunnelTargetsPort);
        Assert.Contains("localhost:8787 rather than Issun on localhost:9000", status.Detail);
    }

    [Fact]
    public async Task Serve_without_funnel_is_not_public()
    {
        var status = await Cli(Running(ServeOnly)).ProbeAsync(8787, CancellationToken.None);

        Assert.False(status.FunnelOn);
        Assert.False(status.FunnelTargetsPort);
        Assert.StartsWith("Funnel is off", status.Detail);
    }

    [Fact]
    public async Task Funnel_on_part_of_the_address_is_not_enough()
    {
        var status = await Cli(Running(FunnelOnlyAPath)).ProbeAsync(8787, CancellationToken.None);

        Assert.False(status.FunnelTargetsPort);
        Assert.StartsWith("Funnel sends only /now-playing to Issun", status.Detail);
    }

    [Fact]
    public async Task Funnel_on_8443_does_not_match_the_address_ammy_is_given()
    {
        var status = await Cli(Running(FunnelOnPort8443)).ProbeAsync(8787, CancellationToken.None);

        Assert.False(status.FunnelTargetsPort);
        Assert.StartsWith("Funnel publishes Issun on port 8443", status.Detail);
    }

    [Fact]
    public async Task A_funnel_started_without_bg_works_but_is_flagged()
    {
        var status = await Cli(Running(FunnelForeground)).ProbeAsync(8787, CancellationToken.None);

        Assert.True(status.FunnelOn);
        Assert.True(status.FunnelTargetsPort);
        Assert.Contains("without --bg", status.Detail);
    }

    [Fact]
    public async Task A_hung_cli_times_out_with_advice()
    {
        var runner = new FakeRunner().Answer(Status, new ProcessResult(true, null, "", "", TimedOut: true));
        using var log = Log.Capture();

        var status = await Cli(runner).ProbeAsync(8787, CancellationToken.None);

        Assert.True(status.Installed);
        Assert.False(status.Running);
        Assert.StartsWith("Tailscale didn't answer within 5 seconds.", status.Detail);
        Assert.Contains("[tailscale] status --json timed out after 5 s", log.Lines);
    }

    [Fact]
    public async Task A_cli_that_cannot_start_is_reported()
    {
        var runner = new FakeRunner().Answer(Status, ProcessResult.NotStarted("Access is denied."));
        using var log = Log.Capture();

        var status = await Cli(runner).ProbeAsync(8787, CancellationToken.None);

        Assert.Equal("Issun couldn't run Tailscale (Access is denied.). Reinstalling Tailscale may fix it.", status.Detail);
        Assert.Contains("[tailscale] could not run tailscale status --json: Access is denied.", log.Lines);
    }

    [Fact]
    public async Task Output_that_is_not_status_json_is_reported_not_trusted()
    {
        var status = await Cli(new FakeRunner().Answer(Status, "hello")).ProbeAsync(8787, CancellationToken.None);

        Assert.False(status.Running);
        Assert.StartsWith("Tailscale answered with something Issun couldn't read.", status.Detail);
    }

    [Fact]
    public async Task Unreadable_funnel_settings_are_reported()
    {
        var status = await Cli(Running("{ nope")).ProbeAsync(8787, CancellationToken.None);

        Assert.True(status.Running);
        Assert.Equal(DnsName, status.DnsName);
        Assert.StartsWith("Tailscale answered with Funnel settings Issun couldn't read.", status.Detail);
    }

    [Fact]
    public async Task Repeated_probes_log_what_changed_not_the_same_line_every_time()
    {
        var off = Running(FunnelOff);
        var cli = Cli(off);
        using var log = Log.Capture();

        await cli.ProbeAsync(8787, CancellationToken.None);
        await cli.ProbeAsync(8787, CancellationToken.None);

        Assert.Single(log.Lines, l => l.StartsWith("[tailscale] Funnel is off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_log_never_carries_the_tailnet_name()
    {
        var runner = new FakeRunner().Answer(Status, StatusRunning)
            .Answer(FunnelStatus, new ProcessResult(true, 1, "", "error fetching config for mypc.example-tailnet.ts.net\n", false));
        using var log = Log.Capture();

        var status = await Cli(runner).ProbeAsync(8787, CancellationToken.None);

        // What the person is shown keeps it; the log does not.
        Assert.Contains("example-tailnet", status.Detail);
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains("example-tailnet", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("mypc.<tailnet>.ts.net", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://mypc.example-tailnet.ts.net/now-playing", "https://mypc.<tailnet>.ts.net/now-playing")]
    [InlineData("MYPC.Example-Tailnet.TS.NET:443", "MYPC.<tailnet>.TS.NET:443")]
    [InlineData("the tailnet is example-tailnet.ts.net", "the tailnet is <tailnet>.ts.net")]
    [InlineData("no hostname here", "no hostname here")]
    public void Redact_masks_the_tailnet_label_only(string text, string expected)
    {
        Assert.Equal(expected, TailscaleCli.Redact(text));
    }

    [Fact]
    public async Task Cancellation_is_passed_through()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cli(Running(FunnelToIssun)).ProbeAsync(8787, cts.Token));
    }

    // ───────────── parsing ─────────────

    [Fact]
    public void Status_parsing_reads_state_name_and_capabilities()
    {
        var self = TailscaleCli.ParseStatus(StatusRunning);

        Assert.NotNull(self);
        Assert.Equal("Running", self.BackendState);
        Assert.Equal(DnsName, self.DnsName);
        Assert.True(self.MagicDnsEnabled);
        Assert.True(self.FunnelAllowed);
        Assert.True(self.HttpsAllowed);
    }

    [Fact]
    public void Status_parsing_falls_back_to_the_deprecated_capability_list()
    {
        var self = TailscaleCli.ParseStatus("""{ "BackendState": "Running", "Self": { "DNSName": "a.b.ts.net.", "Capabilities": ["https"] } }""");

        Assert.NotNull(self);
        Assert.False(self.FunnelAllowed);
        Assert.True(self.HttpsAllowed);
        Assert.Null(self.MagicDnsEnabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "BackendState": 3 }""")]
    [InlineData("not json")]
    public void Status_parsing_rejects_what_is_not_a_status(string json)
    {
        Assert.Null(TailscaleCli.ParseStatus(json));
    }

    [Theory]
    [InlineData("http://localhost:8787", 8787, true)]
    [InlineData("http://localhost:8787/", 8787, true)]
    [InlineData("http://127.0.0.1:8787", 8787, true)]
    [InlineData("http://LOCALHOST:8787", 8787, true)]
    [InlineData("localhost:8787", 8787, true)]
    [InlineData("http://localhost:8788", 8787, false)]
    [InlineData("http://localhost", 8787, false)]
    [InlineData("https://localhost:8787", 8787, false)]
    [InlineData("https+insecure://localhost:8787", 8787, false)]
    [InlineData("http://[::1]:8787", 8787, false)]
    [InlineData("http://192.168.1.10:8787", 8787, false)]
    [InlineData("http://localhost:8787/api", 8787, false)]
    [InlineData("text:hello", 8787, false)]
    public void Only_plain_http_to_loopback_on_issuns_port_targets_issun(string proxy, int port, bool expected)
    {
        Assert.Equal(expected, TailscaleCli.TargetsIssun(proxy, port));
    }

    // ───────────── locating tailscale.exe ─────────────

    [Fact]
    public void Path_is_searched_before_program_files()
    {
        using var dir = new TempFolder();
        var onPath = Directory.CreateDirectory(dir.File("bin")).FullName;
        var programFiles = Directory.CreateDirectory(dir.File("pf")).FullName;
        Directory.CreateDirectory(Path.Combine(programFiles, "Tailscale"));
        File.WriteAllBytes(Path.Combine(onPath, "tailscale.exe"), []);
        File.WriteAllBytes(Path.Combine(programFiles, "Tailscale", "tailscale.exe"), []);

        var path = $"{dir.File("missing")};;\"{onPath}\";";
        Assert.Equal(Path.Combine(onPath, "tailscale.exe"), TailscaleCli.Locate(path, [programFiles]));
    }

    [Fact]
    public void Program_files_is_the_fallback()
    {
        using var dir = new TempFolder();
        var programFiles = Directory.CreateDirectory(dir.File("pf")).FullName;
        Directory.CreateDirectory(Path.Combine(programFiles, "Tailscale"));
        File.WriteAllBytes(Path.Combine(programFiles, "Tailscale", "tailscale.exe"), []);

        Assert.Equal(Path.Combine(programFiles, "Tailscale", "tailscale.exe"),
                     TailscaleCli.Locate(dir.File("empty-bin"), [null, "", programFiles]));
    }

    [Fact]
    public void Nowhere_is_null()
    {
        using var dir = new TempFolder();
        Assert.Null(TailscaleCli.Locate(null, [dir.Path]));
    }

    // ───────────── turning Funnel on (never for real) ─────────────

    private const string EnableArgs = "funnel --bg --https=443 localhost:8787";

    [Fact]
    public async Task Enabling_runs_the_bg_command_with_a_thirty_second_limit()
    {
        var runner = new FakeRunner().Answer(EnableArgs, FakeRunner.Ok(FunnelEnabledOutput));
        using var log = Log.Capture();

        var (ok, output) = await Cli(runner).EnableFunnelAsync(8787, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(FunnelEnabledOutput.TrimEnd(), output);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Exe, call.File);
        Assert.Equal(["funnel", "--bg", "--https=443", "localhost:8787"], call.Args);
        Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
        Assert.Contains("[tailscale] Funnel turned on: https 443 -> localhost:8787", log.Lines);
    }

    [Fact]
    public void The_command_shown_to_people_matches_the_one_run()
    {
        Assert.Equal("tailscale " + EnableArgs, TailscaleCli.FunnelCommand(8787));
    }

    [Fact]
    public async Task The_enable_link_comes_back_verbatim_when_tailscale_waits_for_it()
    {
        var runner = new FakeRunner().Answer(EnableArgs, new ProcessResult(true, null, FunnelNeedsEnabling, "", TimedOut: true));
        using var log = Log.Capture();

        var (ok, output) = await Cli(runner).EnableFunnelAsync(8787, CancellationToken.None);

        Assert.False(ok);
        Assert.StartsWith(FunnelNeedsEnabling.TrimEnd(), output);
        Assert.Contains("https://login.tailscale.com/f/funnel?node=nEXAMPLE1234", output);
        Assert.Contains("If it printed a link above, open it", output);
        Assert.Contains(log.Lines, l => l.StartsWith("[tailscale] tailscale funnel --bg --https=443 localhost:8787 still waiting after 30 s:", StringComparison.Ordinal)
                                        && l.Contains("https://login.tailscale.com/f/funnel?node=nEXAMPLE1234", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_enable_returns_what_tailscale_said()
    {
        const string said = "error: Funnel not available; \"funnel\" node attribute not set.\n";
        var runner = new FakeRunner().Answer(EnableArgs, new ProcessResult(true, 1, "", said, false));
        using var log = Log.Capture();

        var (ok, output) = await Cli(runner).EnableFunnelAsync(8787, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(said.TrimEnd(), output);
        Assert.Contains(log.Lines, l => l.StartsWith("[tailscale] tailscale funnel --bg --https=443 localhost:8787 exited 1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_silent_failure_still_says_something()
    {
        var runner = new FakeRunner().Answer(EnableArgs, new ProcessResult(true, 3, "", "", false));

        var (ok, output) = await Cli(runner).EnableFunnelAsync(8787, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("Tailscale exited with code 3 and said nothing.", output);
    }

    [Fact]
    public async Task Enabling_without_tailscale_runs_nothing()
    {
        var runner = new FakeRunner();
        using var log = Log.Capture();

        var (ok, output) = await Cli(runner, exe: null).EnableFunnelAsync(8787, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("tailscale.com/download", output);
        Assert.Empty(runner.Calls);
        Assert.Contains("[tailscale] not turning Funnel on: tailscale.exe not found on PATH or in Program Files", log.Lines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task Enabling_for_an_impossible_port_runs_nothing(int port)
    {
        var runner = new FakeRunner();

        var (ok, _) = await Cli(runner).EnableFunnelAsync(port, CancellationToken.None);

        Assert.False(ok);
        Assert.Empty(runner.Calls);
    }
}
