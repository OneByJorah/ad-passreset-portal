using PassReset.Common;
using PassReset.Web.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace PassReset.Tests.Windows.Web.Helpers;

public class DebugProviderStatusTests
{
    private static DebugPasswordChangeProvider NewProvider() =>
        new(NullLogger<DebugPasswordChangeProvider>.Instance);

    [Fact]
    public async Task ValidUser_ReturnsAuthenticatedWithResolvedExpiryAndPolicy()
    {
        var status = await NewProvider().GetUserPasswordStatusAsync("validUser", "anything");

        Assert.True(status.Authenticated);
        Assert.Null(status.Error);
        Assert.Equal(ExpirySource.Resolved, status.Source);
        Assert.False(status.NeverExpires);
        Assert.NotNull(status.ExpiresUtc);
        Assert.NotNull(status.Policy);
    }

    [Fact]
    public async Task MagicInvalidCredentials_ReturnsNotAuthenticatedWithPreciseCode()
    {
        var status = await NewProvider().GetUserPasswordStatusAsync("invalidCredentials", "x");

        Assert.False(status.Authenticated);
        Assert.Equal(ApiErrorCode.InvalidCredentials, status.Error);
        Assert.Null(status.ExpiresUtc);
    }

    [Fact]
    public async Task MagicNeverExpires_ReturnsAuthenticatedNeverExpires()
    {
        var status = await NewProvider().GetUserPasswordStatusAsync("neverExpires", "x");

        Assert.True(status.Authenticated);
        Assert.Null(status.Error);
        Assert.True(status.NeverExpires);
        Assert.Null(status.ExpiresUtc);
    }
}
