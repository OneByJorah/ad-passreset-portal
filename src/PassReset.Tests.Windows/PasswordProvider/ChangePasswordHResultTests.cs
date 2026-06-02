using PassReset.Common;
using PassReset.PasswordProvider;

namespace PassReset.Tests.Windows.PasswordProvider;

/// <summary>
/// STAB-004: the HResult classifier maps E_ACCESSDENIED / DS_CONSTRAINT_VIOLATION to
/// PasswordTooRecentlyChanged regardless of whether the exception arrived as a COMException
/// or an UnauthorizedAccessException. Pure helper → no live AD needed.
/// </summary>
public class ChangePasswordHResultTests
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int ERROR_DS_CONSTRAINT_VIOLATION = unchecked((int)0x8007202F);

    [Fact]
    public void AccessDenied_MapsToPasswordTooRecentlyChanged()
    {
        var code = PasswordChangeProvider.ClassifyChangePasswordHResult(E_ACCESSDENIED);
        Assert.Equal(ApiErrorCode.PasswordTooRecentlyChanged, code);
    }

    [Fact]
    public void ConstraintViolation_MapsToPasswordTooRecentlyChanged()
    {
        var code = PasswordChangeProvider.ClassifyChangePasswordHResult(ERROR_DS_CONSTRAINT_VIOLATION);
        Assert.Equal(ApiErrorCode.PasswordTooRecentlyChanged, code);
    }

    [Fact]
    public void UnknownHResult_MapsToNull()
    {
        var code = PasswordChangeProvider.ClassifyChangePasswordHResult(unchecked((int)0x80070057)); // E_INVALIDARG
        Assert.Null(code);
    }

    [Fact]
    public void MapUnauthorizedAccess_AccessDenied_ThrowsPasswordTooRecentlyChanged()
    {
        var ex = new UnauthorizedAccessException("denied")
        { HResult = unchecked((int)0x80070005) };

        var thrown = Assert.Throws<ApiErrorException>(() =>
            PasswordChangeProvider.MapUnauthorizedAccess(ex));
        Assert.Equal(ApiErrorCode.PasswordTooRecentlyChanged, thrown.ErrorCode);
    }

    [Fact]
    public void MapUnauthorizedAccess_PermissionDenied_Rethrows()
    {
        var ex = new UnauthorizedAccessException("genuine permission failure")
        { HResult = unchecked((int)0x80070057) };

        Assert.Throws<UnauthorizedAccessException>(() =>
            PasswordChangeProvider.MapUnauthorizedAccess(ex));
    }
}
