using PassReset.PasswordProvider;

namespace PassReset.Tests.Windows.PasswordProvider;

public class WindowsStatusDecodeTests
{
    [Fact]
    public void Decode_Zero_NeverExpires()
    {
        var (expires, never) = PasswordChangeProvider.DecodeExpiry(0);
        Assert.True(never);
        Assert.Null(expires);
    }

    [Fact]
    public void Decode_Int64Max_NeverExpires()
    {
        var (expires, never) = PasswordChangeProvider.DecodeExpiry(long.MaxValue);
        Assert.True(never);
        Assert.Null(expires);
    }

    [Fact]
    public void Decode_RealFileTime_ReturnsThatInstant()
    {
        var ft = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToFileTime();
        var (expires, never) = PasswordChangeProvider.DecodeExpiry(ft);
        Assert.False(never);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), expires);
    }
}
