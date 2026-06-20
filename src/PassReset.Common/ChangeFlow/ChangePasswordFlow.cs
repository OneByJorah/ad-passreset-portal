namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The Change Flow implementation. Owns the full sequence above the Password Changer seam.
/// Performs no HTTP and no email I/O — it returns intent (see <see cref="NotificationRequest"/>).
/// </summary>
public sealed class ChangePasswordFlow(
    IPasswordChanger changer,
    IRecaptchaVerifier recaptcha,
    ISiemService siem,
    IErrorRedactor redactor,
    IChangeFlowSettings settings) : IChangePasswordFlow
{
    private readonly IPasswordChanger _changer = changer;
    private readonly IRecaptchaVerifier _recaptcha = recaptcha;
    private readonly ISiemService _siem = siem;
    private readonly IErrorRedactor _redactor = redactor;
    private readonly IChangeFlowSettings _settings = settings;

    public async Task<ChangePasswordOutcome> HandleAsync(ChangePasswordRequest request, RequestContext context)
    {
        Audit(SiemEventType.PasswordChangeAttemptStarted, "AttemptStarted", request, context);

        if (_settings.MinimumDistance > 0 &&
            PasswordDistance.Levenshtein(request.CurrentPassword, request.NewPassword) < _settings.MinimumDistance)
        {
            // Matches the controller: "DistanceTooLow" logged WITHOUT a SIEM event type.
            return ChangePasswordOutcome.Validation(new ApiErrorItem(ApiErrorCode.MinimumDistance));
        }

        if (_settings.RecaptchaEnabled &&
            !await _recaptcha.VerifyAsync(request.Recaptcha, _settings.RecaptchaAction, context.ClientIp))
        {
            Audit(SiemEventType.RecaptchaFailed, "RecaptchaFailed", request, context);
            return ChangePasswordOutcome.Captcha(new ApiErrorItem(ApiErrorCode.InvalidCaptcha));
        }

        var error = await _changer.PerformPasswordChangeAsync(
            request.Username, request.CurrentPassword, request.NewPassword);

        if (error is not null)
        {
            Audit(MapErrorCodeToSiemEvent(error.ErrorCode), $"Failed:{error.ErrorCode}", request, context, error.Message);
            return ChangePasswordOutcome.Changed(_redactor.Redact(error));
        }

        Audit(SiemEventType.PasswordChanged, "Success", request, context);

        var notify = _settings.NotificationEnabled
            ? new NotificationRequest(request.Username, DateTime.UtcNow.ToString("u"), context.ClientIp)
            : null;

        return ChangePasswordOutcome.Success("Password changed successfully.", notify);
    }

    private void Audit(SiemEventType eventType, string outcome, ChangePasswordRequest request,
        RequestContext context, string? detail = null) =>
        _siem.LogEvent(new AuditEvent(
            EventType: eventType,
            Outcome:   outcome,
            Username:  request.Username,
            ClientIp:  context.ClientIp,
            TraceId:   context.TraceId,
            Detail:    detail));

    private static SiemEventType MapErrorCodeToSiemEvent(ApiErrorCode code) => code switch
    {
        ApiErrorCode.InvalidCredentials => SiemEventType.InvalidCredentials,
        ApiErrorCode.UserNotFound       => SiemEventType.UserNotFound,
        ApiErrorCode.PortalLockout      => SiemEventType.PortalLockout,
        ApiErrorCode.ApproachingLockout => SiemEventType.ApproachingLockout,
        ApiErrorCode.ChangeNotPermitted => SiemEventType.ChangeNotPermitted,
        _                               => SiemEventType.Generic,
    };
}
