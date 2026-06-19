using PassReset.Common;
using PassReset.PasswordProvider.Ldap;

namespace PassReset.Tests.Services;

public class LdapStatusTests
{
    [Fact]
    public void Decode_Zero_MeansNeverExpires()
    {
        var (expires, never) = LdapPasswordChangeProvider.DecodeExpiry("0");
        Assert.True(never);
        Assert.Null(expires);
    }

    [Fact]
    public void Decode_Int64Max_MeansNeverExpires()
    {
        var (expires, never) = LdapPasswordChangeProvider.DecodeExpiry("9223372036854775807");
        Assert.True(never);
        Assert.Null(expires);
    }

    [Fact]
    public void Decode_RealFileTime_ReturnsThatInstant()
    {
        // 2026-09-01T00:00:00Z as a Windows FILETIME.
        var ft = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToFileTime();
        var (expires, never) = LdapPasswordChangeProvider.DecodeExpiry(ft.ToString());
        Assert.False(never);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), expires);
    }

    [Fact]
    public void Decode_Garbage_ReturnsNeitherExpiryNorNever()
    {
        var (expires, never) = LdapPasswordChangeProvider.DecodeExpiry("not-a-number");
        Assert.False(never);
        Assert.Null(expires);
    }
}
