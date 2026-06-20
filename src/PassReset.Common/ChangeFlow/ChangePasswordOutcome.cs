namespace PassReset.Common.ChangeFlow;

/// <summary>How the Change Flow concluded — drives the controller's HTTP mapping.</summary>
public enum Disposition
{
    /// <summary>Change succeeded.</summary>
    Ok,
    /// <summary>A flow-owned validation rule rejected the request (e.g. minimum distance).</summary>
    ValidationError,
    /// <summary>reCAPTCHA verification failed.</summary>
    CaptchaRejected,
    /// <summary>The credentialed change itself failed (provider returned an error).</summary>
    ChangeFailed,
}

/// <summary>
/// The intent to send a password-changed notification. Carries inputs only — the
/// controller resolves the recipient and renders the body off the response path so the
/// synchronous directory lookup never blocks the HTTP response.
/// </summary>
public sealed record NotificationRequest(string Username, string Timestamp, string ClientIp);

/// <summary>
/// The full result of running the Change Flow. The <see cref="Error"/> is ALREADY redacted
/// per the Error Redaction seam — the controller serializes it verbatim.
/// </summary>
public sealed record ChangePasswordOutcome(
    Disposition Disposition,
    ApiErrorItem? Error,
    string? SuccessMessage,
    NotificationRequest? Notification)
{
    public static ChangePasswordOutcome Success(string message, NotificationRequest? notify) =>
        new(Disposition.Ok, null, message, notify);

    public static ChangePasswordOutcome Validation(ApiErrorItem error) =>
        new(Disposition.ValidationError, error, null, null);

    public static ChangePasswordOutcome Captcha(ApiErrorItem error) =>
        new(Disposition.CaptchaRejected, error, null, null);

    public static ChangePasswordOutcome Changed(ApiErrorItem error) =>
        new(Disposition.ChangeFailed, error, null, null);
}
