namespace Issun.Core.Artwork;

/// <summary>
/// Python's <c>base64.b64decode(s, validate=True)</c>, which on the Python
/// relay.py runs under (3.14) is <c>binascii.a2b_base64(s, strict_mode=True)</c>.
///
/// Convert.FromBase64String is not a substitute: it skips whitespace that
/// strict mode rejects, and the point of relay.py asking for validate=True was
/// to refuse anything that isn't cleanly base64 before writing it to disk.
///
/// The error wording follows 3.14, which differs from 3.11–3.13 in which
/// message it picks for a stray "=" (not in what it accepts). It was checked
/// against every string of up to seven characters over "AQ=-\n"; the reason
/// ends up in the "[art] uploaded cover … rejected" log line.
/// </summary>
internal static class PyBase64
{
    /// <summary>
    /// Decodes <paramref name="s"/>, or returns null with <paramref name="error"/>
    /// set to the message Python's binascii.Error (or ValueError) would carry.
    /// </summary>
    public static byte[]? DecodeStrict(string s, out string? error)
    {
        error = null;
        foreach (var c in s)
        {
            if (c > 0x7F)
            {
                error = "string argument should contain only ASCII characters";
                return null;
            }
        }

        if (s.Length > 0 && s[0] == '=')
        {
            error = "Leading padding not allowed";
            return null;
        }

        var output = new byte[(s.Length + 3) / 4 * 3];
        var written = 0;
        var quadPos = 0;
        var leftChar = 0;
        var pads = 0;
        var paddingStarted = false;

        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '=')
            {
                paddingStarted = true;
                if (quadPos == 0)
                {
                    error = "Excess padding not allowed";
                    return null;
                }
                if (quadPos == 1)
                {
                    // One data character then padding can never have come
                    // from an encoder; 3.14 says so at the "=" itself.
                    error = LengthError(written);
                    return null;
                }
                if (quadPos + ++pads >= 4)
                {
                    // A complete pad sequence ends the input; strict mode
                    // refuses anything after it, naming what it found first.
                    if (i + 1 < s.Length)
                    {
                        var next = s[i + 1];
                        error = next == '=' ? "Excess padding not allowed"
                            : Sextet(next) >= 0 ? "Excess data after padding"
                            : "Only base64 data is allowed";
                        return null;
                    }
                    return output[..written];
                }
                continue;
            }

            var value = Sextet(ch);
            if (value < 0)
            {
                error = "Only base64 data is allowed";
                return null;
            }
            if (paddingStarted)
            {
                error = "Discontinuous padding not allowed";
                return null;
            }
            pads = 0;

            switch (quadPos)
            {
                case 0:
                    quadPos = 1;
                    leftChar = value;
                    break;
                case 1:
                    quadPos = 2;
                    output[written++] = (byte)((leftChar << 2) | (value >> 4));
                    leftChar = value & 0x0F;
                    break;
                case 2:
                    quadPos = 3;
                    output[written++] = (byte)((leftChar << 4) | (value >> 2));
                    leftChar = value & 0x03;
                    break;
                default:
                    quadPos = 0;
                    output[written++] = (byte)((leftChar << 6) | value);
                    leftChar = 0;
                    break;
            }
        }

        if (quadPos == 1)
        {
            error = LengthError(written);
            return null;
        }
        if (quadPos != 0)
        {
            error = "Incorrect padding";
            return null;
        }
        return output[..written];
    }

    private static string LengthError(int written) =>
        "Invalid base64-encoded string: number of data characters "
        + $"({(written / 3 * 4 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}) "
        + "cannot be 1 more than a multiple of 4";

    private static int Sextet(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };
}
