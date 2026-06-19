namespace PassReset.Common;

/// <summary>
/// The Directory User Reader seam: the unauthenticated directory-read path used by
/// side-effects (the password-changed email and the expiry-notification background service).
/// Reads directory facts without binding as the user.
/// </summary>
public interface IDirectoryUserReader
{
    /// <summary>
    /// Retrieves the email address for the specified user from the directory.
    /// Returns null if the user is not found or on error.
    /// </summary>
    string? GetUserEmail(string username);

    /// <summary>
    /// Returns user details for all members of the specified AD group (recursive).
    /// Used by the password expiry notification background service.
    /// </summary>
    IEnumerable<(string Username, string Email, DateTime? PasswordLastSet)> GetUsersInGroup(string groupName);

    /// <summary>
    /// Returns the domain maximum password age (maxPwdAge).
    /// Returns TimeSpan.MaxValue if the domain has no password expiry policy.
    /// </summary>
    TimeSpan GetDomainMaxPasswordAge();
}
