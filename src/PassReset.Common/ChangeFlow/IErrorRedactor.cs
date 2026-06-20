namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The Error Redaction seam (STAB-013): decides whether a precise failure code may reach
/// the client or must collapse to <see cref="ApiErrorCode.Generic"/>. Decoupled from the
/// hosting concept ("are we in production?") that triggers it — that lives in the adapter.
/// </summary>
public interface IErrorRedactor
{
    /// <summary>
    /// Returns the error to put on the wire: the original, or a Generic collapse when the
    /// adapter's policy says enumeration codes must be redacted in the current environment.
    /// SIEM granularity is preserved independently of this result.
    /// </summary>
    ApiErrorItem Redact(ApiErrorItem error);

    /// <summary>
    /// STAB-013 D-01: account-enumeration codes that leak whether a username exists in AD.
    /// <see cref="ApiErrorCode.InvalidCredentials"/> and <see cref="ApiErrorCode.UserNotFound"/>
    /// are enumeration vectors. ApproachingLockout/PortalLockout are NOT — they leak only
    /// per-account portal throttling state, never directory membership.
    /// </summary>
    static bool IsAccountEnumerationCode(ApiErrorCode code) =>
        code is ApiErrorCode.InvalidCredentials or ApiErrorCode.UserNotFound;
}
