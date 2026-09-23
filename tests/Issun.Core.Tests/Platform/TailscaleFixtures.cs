namespace Issun.Core.Tests.Platform;

/// <summary>
/// Output of the two read-only probes, captured from Tailscale 1.102.3 on the
/// owner's PC in September 2026 while Funnel was forwarding to localhost:8787,
/// then trimmed. The tailnet is replaced by example-tailnet and the machine by
/// mypc; addresses, keys and the account name are gone. The variants below
/// change only what each test is about.
/// </summary>
internal static class TailscaleFixtures
{
    public const string DnsName = "mypc.example-tailnet.ts.net";

    public const string StatusRunning = """
        {
          "Version": "1.102.3-t9329c3677-ga522f65e9",
          "TUN": true,
          "BackendState": "Running",
          "HaveNodeKey": true,
          "AuthURL": "",
          "TailscaleIPs": ["100.64.0.1", "fd7a:115c:a1e0::1"],
          "Self": {
            "ID": "nREDACTED",
            "HostName": "MyPC",
            "DNSName": "mypc.example-tailnet.ts.net.",
            "OS": "windows",
            "Online": true,
            "Capabilities": [
              "HTTPS://TAILSCALE.COM/s/DEPRECATED-NODE-CAPS#see-https://github.com/tailscale/tailscale/issues/11508",
              "default-auto-update",
              "funnel",
              "https",
              "https://tailscale.com/cap/funnel-ports?ports=443,8443,10000",
              "https://tailscale.com/cap/is-admin"
            ],
            "CapMap": {
              "default-auto-update": null,
              "funnel": null,
              "https": null,
              "https://tailscale.com/cap/funnel-ports?ports=443,8443,10000": null,
              "https://tailscale.com/cap/is-admin": null
            }
          },
          "Health": [],
          "MagicDNSSuffix": "example-tailnet.ts.net",
          "CurrentTailnet": {
            "Name": "user@example.com",
            "MagicDNSSuffix": "example-tailnet.ts.net",
            "MagicDNSEnabled": true
          },
          "CertDomains": ["mypc.example-tailnet.ts.net"],
          "Peer": {},
          "User": {}
        }
        """;

    /// <summary>A tailnet whose policy doesn't grant Funnel and has no HTTPS certificates yet.</summary>
    public static readonly string StatusRunningWithoutFunnelCaps = StatusRunning
        .Replace("\"funnel\": null,", "", StringComparison.Ordinal)
        .Replace("\"https\": null,", "", StringComparison.Ordinal);

    public static readonly string StatusMagicDnsOff = StatusRunning
        .Replace("\"MagicDNSEnabled\": true", "\"MagicDNSEnabled\": false", StringComparison.Ordinal);

    public const string StatusNeedsLogin = """
        {
          "Version": "1.102.3-t9329c3677-ga522f65e9",
          "BackendState": "NeedsLogin",
          "AuthURL": "",
          "Self": { "HostName": "MyPC", "DNSName": "", "OS": "windows", "Online": false },
          "Health": ["You are logged out."],
          "MagicDNSSuffix": "",
          "CurrentTailnet": null,
          "Peer": null
        }
        """;

    public static readonly string StatusStopped = StatusRunning
        .Replace("\"BackendState\": \"Running\"", "\"BackendState\": \"Stopped\"", StringComparison.Ordinal);

    /// <summary>What the CLI prints on stderr when the Tailscale service isn't running.</summary>
    public const string DaemonNotRunning =
        "failed to connect to local Tailscale daemon for /localapi/v0/status; not running? "
        + "Error: dial tcp 127.0.0.1:41112: connectex: No connection could be made because the target machine actively refused it.\n";

    /// <summary>tailscale funnel status --json, as captured: the whole site, on 443, to localhost:8787.</summary>
    public const string FunnelToIssun = """
        {
          "TCP": {
            "443": {
              "HTTPS": true
            }
          },
          "Web": {
            "mypc.example-tailnet.ts.net:443": {
              "Handlers": {
                "/": {
                  "Proxy": "http://localhost:8787"
                }
              }
            }
          },
          "AllowFunnel": {
            "mypc.example-tailnet.ts.net:443": true
          }
        }
        """;

    /// <summary>Nothing served or funnelled.</summary>
    public const string FunnelOff = "{}\n";

    public static readonly string FunnelToLoopbackIp = FunnelToIssun
        .Replace("http://localhost:8787", "http://127.0.0.1:8787", StringComparison.Ordinal);

    public static readonly string FunnelToOtherPort = FunnelToIssun
        .Replace("http://localhost:8787", "http://127.0.0.1:3000", StringComparison.Ordinal);

    /// <summary>`tailscale serve`: the same route, reachable inside the tailnet only.</summary>
    public static readonly string ServeOnly = """
        {
          "TCP": { "443": { "HTTPS": true } },
          "Web": {
            "mypc.example-tailnet.ts.net:443": {
              "Handlers": { "/": { "Proxy": "http://localhost:8787" } }
            }
          }
        }
        """;

    public const string FunnelOnlyAPath = """
        {
          "TCP": { "443": { "HTTPS": true } },
          "Web": {
            "mypc.example-tailnet.ts.net:443": {
              "Handlers": { "/now-playing": { "Proxy": "http://localhost:8787" } }
            }
          },
          "AllowFunnel": { "mypc.example-tailnet.ts.net:443": true }
        }
        """;

    public const string FunnelOnPort8443 = """
        {
          "TCP": { "8443": { "HTTPS": true } },
          "Web": {
            "mypc.example-tailnet.ts.net:8443": {
              "Handlers": { "/": { "Proxy": "http://localhost:8787" } }
            }
          },
          "AllowFunnel": { "mypc.example-tailnet.ts.net:8443": true }
        }
        """;

    /// <summary>`tailscale funnel 8787` typed in a terminal without --bg: lives in Foreground, keyed by session.</summary>
    public const string FunnelForeground = """
        {
          "Foreground": {
            "a1b2c3d4e5f6": {
              "TCP": { "443": { "HTTPS": true } },
              "Web": {
                "mypc.example-tailnet.ts.net:443": {
                  "Handlers": { "/": { "Proxy": "http://127.0.0.1:8787" } }
                }
              },
              "AllowFunnel": { "mypc.example-tailnet.ts.net:443": true }
            }
          }
        }
        """;

    /// <summary>
    /// What `tailscale funnel --bg` prints when the tailnet hasn't allowed Funnel
    /// yet, before it sits waiting for the link to be opened. The node ID is a
    /// placeholder.
    /// </summary>
    public const string FunnelNeedsEnabling =
        "Funnel is not enabled on your tailnet.\n"
        + "To enable, visit:\n\n"
        + "         https://login.tailscale.com/f/funnel?node=nEXAMPLE1234\n";

    public const string FunnelEnabledOutput =
        "Available on the internet:\n\n"
        + "https://mypc.example-tailnet.ts.net/\n"
        + "|-- proxy http://localhost:8787\n\n"
        + "Funnel started and running in the background.\n"
        + "To disable the proxy, run: tailscale funnel --https=443 off\n";
}
