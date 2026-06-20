using PassReset.Common;
using PassReset.Common.ChangeFlow;
using Xunit;

namespace PassReset.Tests.ChangeFlow;

public class ErrorRedactorTests
{
    // A cross-platform redactor that mirrors the production rule without IHostEnvironment.
    private sealed class FixedModeErrorRedactor(bool redact) : IErrorRedactor
    {
        public ApiErrorItem Redact(ApiErrorItem error) =>
            redact && IErrorRedactor.IsAccountEnumerationCode(error.ErrorCode)
                ? new ApiErrorItem(ApiErrorCode.Generic, error.Message)
                : error;
    }

    [Theory]
    [InlineData(ApiErrorCode.InvalidCredentials, true)]
    [InlineData(ApiErrorCode.UserNotFound, true)]
    [InlineData(ApiErrorCode.ApproachingLockout, false)]
    [InlineData(ApiErrorCode.PortalLockout, false)]
    [InlineData(ApiErrorCode.ChangeNotPermitted, false)]
    public void IsAccountEnumerationCode_ClassifiesCorrectly(ApiErrorCode code, bool expected) =>
        Assert.Equal(expected, IErrorRedactor.IsAccountEnumerationCode(code));

    [Fact]
    public void Redact_WhenRedacting_CollapsesInvalidCredentialsToGeneric()
    {
        var redactor = new FixedModeErrorRedactor(redact: true);
        var result = redactor.Redact(new ApiErrorItem(ApiErrorCode.InvalidCredentials));
        Assert.Equal(ApiErrorCode.Generic, result.ErrorCode);
    }

    [Fact]
    public void Redact_WhenRedacting_PreservesLockoutCodes()
    {
        var redactor = new FixedModeErrorRedactor(redact: true);
        var result = redactor.Redact(new ApiErrorItem(ApiErrorCode.ApproachingLockout));
        Assert.Equal(ApiErrorCode.ApproachingLockout, result.ErrorCode);
    }

    [Fact]
    public void Redact_WhenNotRedacting_PreservesInvalidCredentials()
    {
        var redactor = new FixedModeErrorRedactor(redact: false);
        var result = redactor.Redact(new ApiErrorItem(ApiErrorCode.InvalidCredentials));
        Assert.Equal(ApiErrorCode.InvalidCredentials, result.ErrorCode);
    }
}
