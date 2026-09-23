using Issun.Core.Platform;

namespace Issun.Core.Tests.Platform;

public class KeyGeneratorTests
{
    [Fact]
    public void A_key_is_32_letters_and_digits()
    {
        var key = KeyGenerator.New();
        Assert.Equal(32, key.Length);
        Assert.All(key, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"'{c}' is not typeable on a phone's first keyboard layers"));
    }

    [Fact]
    public void The_alphabet_is_exactly_the_62_letters_and_digits()
    {
        Assert.Equal(62, KeyGenerator.Alphabet.Distinct().Count());
        Assert.All(KeyGenerator.Alphabet, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void Keys_do_not_repeat_and_use_the_whole_alphabet()
    {
        var keys = Enumerable.Range(0, 2000).Select(_ => KeyGenerator.New()).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        // 64,000 characters: every one of the 62 should turn up roughly 1,032
        // times. A modulo-biased or truncated generator shows here as a
        // character that never appears or one that appears far too often.
        var counts = string.Concat(keys).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(62, counts.Count);
        Assert.All(counts.Values, n => Assert.InRange(n, 800, 1300));
    }
}
