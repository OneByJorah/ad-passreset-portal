namespace PassReset.Common;

/// <summary>Where the expiry value came from, for honest UI messaging.</summary>
public enum ExpirySource
{
    /// <summary>Per-user resolved via msDS-UserPasswordExpiryTimeComputed.</summary>
    Resolved,
    /// <summary>Degraded to the domain default (maxPwdAge) — may be wrong under a PSO.</summary>
    DomainDefault,
    /// <summary>Expiry could not be determined at all.</summary>
    Unknown,
}

/// <summary>
/// Result of a Status Check: the outcome of the authenticating bind plus, on success,
/// the user's password expiry and the live AD policy. Never throws — failures are
/// carried in <see cref="Error"/>. <see cref="NeverExpires"/> with a null
/// <see cref="ExpiresUtc"/> is a SUCCESS, not an error.
/// </summary>
public sealed record PasswordStatus(
    bool Authenticated,
    ApiErrorCode? Error,
    DateTimeOffset? ExpiresUtc,
    bool NeverExpires,
    ExpirySource Source,
    PasswordPolicy? Policy);
