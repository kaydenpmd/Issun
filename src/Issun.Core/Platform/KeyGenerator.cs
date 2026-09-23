using System.Security.Cryptography;

namespace Issun.Core.Platform;

/// <summary>
/// The shared key Ammy sends as X-Relay-Key. Replaces the relay-era routine of
/// generating one in PowerShell and writing it straight into .env — which
/// existed so the value never passed through a chat or a screenshot. Issun
/// generates its own and never logs it; only its length is ever printed.
/// </summary>
public static class KeyGenerator
{
    /// <summary>The same length as the relay-era key, so nothing about pairing looks different.</summary>
    public const int Length = 32;

    /// <summary>
    /// Letters and digits only: every character is on a phone keyboard's first
    /// two layers, so the key can be typed into Ammy's Key field by hand if it
    /// has to be. 62^32 is about 190 bits — punctuation would add nothing but typos.
    /// </summary>
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// A fresh key from the OS CSPRNG. RandomNumberGenerator.GetString picks each
    /// character uniformly, so there is no modulo bias toward the start of the
    /// alphabet.
    /// </summary>
    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);
}
