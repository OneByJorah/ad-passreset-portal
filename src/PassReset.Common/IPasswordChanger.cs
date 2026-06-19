namespace PassReset.Common;

/// <summary>
/// The Password Changer seam: the credentialed write path. Authenticates the user with
/// their current password and changes their own password. The only seam wrapped by the
/// lockout and Local Policy decorators.
/// </summary>
public interface IPasswordChanger
{
    /// <summary>
    /// Performs the password change using the credentials provided.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <returns>The API error item, or null if the change succeeded.</returns>
    Task<ApiErrorItem?> PerformPasswordChangeAsync(string username, string currentPassword, string newPassword);
}
