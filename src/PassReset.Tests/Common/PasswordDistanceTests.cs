using PassReset.Common;
using Xunit;

namespace PassReset.Tests.Common;

public class PasswordDistanceTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("Passw0rd!", "Passw0rd!", 0)]
    [InlineData("Passw0rd!", "Passw0rd?", 1)]
    public void Levenshtein_MatchesKnownDistances(string a, string b, int expected)
    {
        Assert.Equal(expected, PasswordDistance.Levenshtein(a, b));
    }
}
