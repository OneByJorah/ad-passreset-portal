namespace PassReset.Common;

/// <summary>
/// The Password Status Reader seam: the credentialed read path serving a Status Check.
/// Authenticates and returns resolved expiry plus the effective AD policy. Read-only.
/// </summary>
public interface IPasswordStatusReader
{
    /// <summary>
    /// Status Check (v2.1): authenticates the user with their current password and, on
    /// success, returns their resolved password expiry plus the effective AD policy. Reuses
    /// the same bind as a Password Change. Never throws — bind failures are returned via
    /// <see cref="PasswordStatus.Error"/> with the precise code; the controller redacts for the wire.
    /// </summary>
    Task<PasswordStatus> GetUserPasswordStatusAsync(string username, string currentPassword);

    /// <summary>
    /// Returns the effective default-domain password policy from RootDSE,
    /// or null if the AD query fails. Implementations must not throw.
    /// </summary>
    Task<PasswordPolicy?> GetEffectivePasswordPolicyAsync();
}
