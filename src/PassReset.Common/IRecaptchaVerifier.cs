namespace PassReset.Common;

/// <summary>
/// Verifies a reCAPTCHA v3 token against the configured provider. Returns true when the
/// request should be allowed (token valid and human-scored, or service unavailable and
/// fail-open is enabled), false when it should be rejected. Never throws.
/// </summary>
public interface IRecaptchaVerifier
{
    /// <summary>
    /// Verifies <paramref name="token"/> for the expected <paramref name="action"/>.
    /// </summary>
    /// <param name="token">The reCAPTCHA token from the client.</param>
    /// <param name="action">The expected reCAPTCHA action (e.g. "change_password").</param>
    /// <param name="clientIp">The client IP, forwarded to the provider and used in logs.</param>
    /// <returns>True to allow the request, false to reject it.</returns>
    Task<bool> VerifyAsync(string token, string action, string clientIp);
}
