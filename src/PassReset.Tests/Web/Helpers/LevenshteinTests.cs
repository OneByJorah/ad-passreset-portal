using PassReset.Common;

namespace PassReset.Tests.Web.Helpers;

/// <summary>
/// The Levenshtein implementation lives on <see cref="PasswordDistance.Levenshtein"/>.
/// </summary>
public class LevenshteinTests
{
    private static int Distance(string a, string b) =>
        PasswordDistance.Levenshtein(a, b);

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("abc", "abd", 1)]    // substitute
    [InlineData("abc", "abcd", 1)]   // insert
    [InlineData("abcd", "abc", 1)]   // delete
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("abc", "ABC", 3)]    // case sensitive by design
    public void Distance_ClassicCases(string a, string b, int expected)
    {
        Assert.Equal(expected, Distance(a, b));
    }

    [Fact]
    public void Distance_IsSymmetric()
    {
        Assert.Equal(Distance("password1", "password2"), Distance("password2", "password1"));
    }
}
