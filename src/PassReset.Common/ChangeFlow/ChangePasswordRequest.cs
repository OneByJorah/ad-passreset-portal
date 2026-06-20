namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The validated inputs to a Password Change, mapped by the controller from its
/// framework-bound model after ModelState validation has already passed.
/// </summary>
public sealed record ChangePasswordRequest(string Username, string CurrentPassword, string NewPassword)
{
    /// <summary>The reCAPTCHA token from the client (empty when reCAPTCHA is disabled).</summary>
    public string Recaptcha { get; init; } = string.Empty;
}

/// <summary>
/// Hosting-flavored correlation values passed in by the controller so the flow never
/// reaches for ASP.NET ambient state (Activity.Current, HttpContext).
/// </summary>
public sealed record RequestContext(string ClientIp, string TraceId);
