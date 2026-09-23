using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Issun.Core.Server;

internal enum KeyVerdict { Accepted, NoKeyConfigured, NoKeySent, WrongKey }

/// <summary>relay.py's <c>Handler._authorised()</c>.</summary>
internal static class KeyCheck
{
    public const string KeyHeader = "X-Relay-Key";

    /// <summary>The old name, still accepted so a phone on an older build keeps working after Issun replaces the relay.</summary>
    public const string LegacyHeader = "X-Relay-Secret";

    /// <param name="configuredKey">Settings.Key, read for this request — a regenerated key applies to the very next one.</param>
    public static KeyVerdict Check(IHeaderDictionary headers, string configuredKey)
    {
        // relay.py stripped RELAY_KEY as it read it. An empty key refuses
        // everything: a receiver with no key configured should be unreachable,
        // not reachable by anyone.
        var key = configuredKey.Trim();
        if (key.Length == 0)
            return KeyVerdict.NoKeyConfigured;

        // headers.get("X-Relay-Key") or headers.get("X-Relay-Secret") or "" —
        // Python's `or`, so an X-Relay-Key that is present but empty falls
        // through to X-Relay-Secret instead of being compared. Where a header
        // is repeated, the first one counts, as http.client's .get() did.
        var supplied = First(headers, KeyHeader) is { Length: > 0 } current ? current
            : First(headers, LegacyHeader) ?? "";
        if (supplied.Length == 0)
            return KeyVerdict.NoKeySent;

        // Constant time, which relay.py's comment claimed and its `==` wasn't.
        // Hashing first means the comparison doesn't even leak the key's length.
        var match = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return match ? KeyVerdict.Accepted : KeyVerdict.WrongKey;
    }

    public static string Describe(KeyVerdict verdict) => verdict switch
    {
        KeyVerdict.NoKeyConfigured => "no key is set in Issun, so every authenticated request is refused",
        KeyVerdict.NoKeySent => "the request carried no key",
        KeyVerdict.WrongKey => "the key did not match",
        _ => "accepted",
    };

    private static string? First(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
