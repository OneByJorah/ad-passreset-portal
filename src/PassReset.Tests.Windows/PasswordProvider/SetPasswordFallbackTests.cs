using PassReset.Common;
using PassReset.PasswordProvider;

namespace PassReset.Tests.Windows.PasswordProvider;

/// <summary>
/// Covers the <c>SetPassword</c> administrative-reset fallback gating (the
/// <c>AllowSetPasswordFallback</c> behavior). The fallback bypasses AD password-history
/// enforcement, so it is permitted only with explicit credentials AND an explicit opt-in.
///
/// The principal types AccountManagement uses (<c>UserPrincipal</c> / <c>AuthenticablePrincipal</c>)
/// are sealed and cannot be faked, so the live ChangePassword→COMException→SetPassword path is not
/// unit-testable. Instead we pin the two pure, logger-free decisions the provider extracts:
/// the gating predicate <see cref="PasswordChangeProvider.ShouldFallBackToSetPassword"/> and the
/// HResult classifier that guarantees a minimum-password-age rejection never reaches the fallback.
/// </summary>
public class SetPasswordFallbackTests
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int ERROR_DS_CONSTRAINT_VIOLATION = unchecked((int)0x8007202F);

    // ── Gating predicate: only (explicit credentials AND opt-in) permits the fallback ──

    [Fact]
    public void Gate_ExplicitCredentials_AndOptIn_AllowsFallback()
    {
        Assert.True(PasswordChangeProvider.ShouldFallBackToSetPassword(
            useAutomaticContext: false, allowSetPasswordFallback: true));
    }

    [Fact]
    public void Gate_OptInButAutomaticContext_DeniesFallback()
    {
        // Automatic (domain-joined) context never falls back, even with the flag on.
        Assert.False(PasswordChangeProvider.ShouldFallBackToSetPassword(
            useAutomaticContext: true, allowSetPasswordFallback: true));
    }

    [Fact]
    public void Gate_ExplicitCredentialsButFlagOff_DeniesFallback()
    {
        Assert.False(PasswordChangeProvider.ShouldFallBackToSetPassword(
            useAutomaticContext: false, allowSetPasswordFallback: false));
    }

    [Fact]
    public void Gate_DefaultPosture_DeniesFallback()
    {
        // Default options: AllowSetPasswordFallback=false. Fallback must be off regardless of context.
        Assert.False(PasswordChangeProvider.ShouldFallBackToSetPassword(
            useAutomaticContext: false, allowSetPasswordFallback: false));
        Assert.False(PasswordChangeProvider.ShouldFallBackToSetPassword(
            useAutomaticContext: true, allowSetPasswordFallback: false));
    }

    // ── Min-age guard: policy-violation HResults are classified BEFORE the gate, so they ──
    // ── can never be routed through the history-bypassing SetPassword fallback.          ──

    [Theory]
    [InlineData(E_ACCESSDENIED)]
    [InlineData(ERROR_DS_CONSTRAINT_VIOLATION)]
    public void MinAgeHResult_IsClassified_SoItNeverReachesFallback(int hresult)
    {
        // The provider checks ClassifyChangePasswordHResult first; a non-null result short-circuits
        // to PasswordTooRecentlyChanged and returns before the SetPassword gate is consulted.
        var classified = PasswordChangeProvider.ClassifyChangePasswordHResult(hresult);
        Assert.Equal(ApiErrorCode.PasswordTooRecentlyChanged, classified);
    }

    [Fact]
    public void GenericChangeFailure_IsNotClassified_SoItIsTheCodePathThatConsultsTheGate()
    {
        // A generic protocol-level rejection (e.g. service account lacks "Change Password" right)
        // is NOT a min-age violation, so classification returns null and the gate decides
        // rethrow-vs-SetPassword. This is the only HResult shape that can reach the fallback.
        var classified = PasswordChangeProvider.ClassifyChangePasswordHResult(unchecked((int)0x80070057)); // E_INVALIDARG
        Assert.Null(classified);
    }
}
