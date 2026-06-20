using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PassReset.Common;
using PassReset.Common.ChangeFlow;
using PassReset.PasswordProvider;
using PassReset.Web.Models;
using PassReset.Web.Services;

namespace PassReset.Web.Controllers;

/// <summary>
/// Handles password change requests and exposes client configuration.
/// Rate-limited to 5 requests per 5 minutes per IP on the POST endpoint.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class PasswordController : ControllerBase
{
    private readonly IPasswordChanger _changer;
    private readonly IPasswordStatusReader _statusReader;
    private readonly IDirectoryUserReader _directoryReader;
    private readonly IEmailService _emailService;
    private readonly ISiemService _siemService;
    private readonly IOptions<ClientSettings> _clientSettings;
    private readonly IOptions<EmailNotificationSettings> _emailNotifSettings;
    private readonly PasswordPolicyCache _policyCache;
    private readonly IPwnedPasswordChecker _pwnedChecker;
    private readonly PasswordChangeOptions _passwordOptions;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<PasswordController> _logger;
    private readonly IRecaptchaVerifier _recaptchaVerifier;
    private readonly IChangePasswordFlow _changeFlow;

    // Pre-compiled 5-char hex regex for pwned-check prefix validation.
    private static readonly Regex Sha1PrefixRegex =
        new("^[a-fA-F0-9]{5}$", RegexOptions.Compiled);

    public PasswordController(
        IPasswordChanger changer,
        IPasswordStatusReader statusReader,
        IDirectoryUserReader directoryReader,
        IEmailService emailService,
        ISiemService siemService,
        IOptions<ClientSettings> clientSettings,
        IOptions<EmailNotificationSettings> emailNotifSettings,
        PasswordPolicyCache policyCache,
        IPwnedPasswordChecker pwnedChecker,
        IOptions<PasswordChangeOptions> passwordOptions,
        IHostEnvironment hostEnvironment,
        IRecaptchaVerifier recaptchaVerifier,
        IChangePasswordFlow changeFlow,
        ILogger<PasswordController> logger)
    {
        _changer         = changer;
        _statusReader    = statusReader;
        _directoryReader = directoryReader;
        _emailService       = emailService;
        _siemService        = siemService;
        _clientSettings     = clientSettings;
        _emailNotifSettings = emailNotifSettings;
        _policyCache        = policyCache;
        _pwnedChecker       = pwnedChecker;
        _passwordOptions    = passwordOptions.Value;
        _hostEnvironment    = hostEnvironment;
        _recaptchaVerifier  = recaptchaVerifier;
        _changeFlow         = changeFlow;
        _logger             = logger;
    }

    /// <summary>
    /// Returns the client-facing configuration (UI strings, feature flags, reCAPTCHA site key).
    /// GET /api/password
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(_clientSettings.Value);

    /// <summary>
    /// Returns the effective default-domain password policy from RootDSE (FEAT-002).
    /// Returns 404 when ShowAdPasswordPolicy is disabled or the AD query fails — the UI
    /// fails closed and renders nothing.
    /// </summary>
    [HttpGet("policy")]
    [EnableRateLimiting("password-fixed-window")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPolicyAsync()
    {
        if (!_clientSettings.Value.ShowAdPasswordPolicy) return NotFound();
        var policy = await _policyCache.GetOrFetchAsync();
        return policy is null ? NotFound() : Ok(policy);
    }

    /// <summary>
    /// FEAT-004: HIBP k-anonymity pre-check.
    /// Accepts a 5-char SHA-1 hex prefix, proxies to the HIBP range API, and returns
    /// the raw suffix list. The client performs the suffix match locally so the server
    /// never learns which suffix matched. Plaintext never leaves the browser.
    /// Rate-limited via the <c>pwned-check-window</c> policy (20/5min/IP).
    /// Honors <see cref="PasswordChangeOptions.FailOpenOnPwnedCheckUnavailable"/>.
    /// POST /api/password/pwned-check
    /// </summary>
    [HttpPost("pwned-check")]
    [EnableRateLimiting("pwned-check-window")]
    [RequestSizeLimit(64)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PwnedCheckAsync([FromBody] PwnedCheckRequest req, CancellationToken ct)
    {
        if (req is null || req.Prefix is null || req.Prefix.Length != 5 || !Sha1PrefixRegex.IsMatch(req.Prefix))
            return BadRequest();

        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var (body, unavailable) = await _pwnedChecker.FetchRangeAsync(req.Prefix, ct);
        if (unavailable)
        {
            _siemService.LogEvent(
                SiemEventType.Generic,
                "pwned-check",
                clientIp,
                $"HIBP range fetch unavailable; FailOpen={_passwordOptions.FailOpenOnPwnedCheckUnavailable}");

            if (_passwordOptions.FailOpenOnPwnedCheckUnavailable)
                return Ok(new { suffixes = string.Empty, unavailable = true });

            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { suffixes = string.Empty, unavailable = true });
        }

        return Ok(new { suffixes = body, unavailable = false });
    }

    /// <summary>
    /// Changes the password for the specified user account.
    /// POST /api/password
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("password-fixed-window")]
    [RequestSizeLimit(8192)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> PostAsync([FromBody] ChangePasswordModel model)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!ModelState.IsValid)
        {
            _siemService.LogEvent(SiemEventType.ValidationFailed, model.Username, clientIp);
            return BadRequest(ApiResult.FromModelStateErrors(ModelState));
        }

        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "unknown";
        using var requestScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Username"] = model.Username,
            ["TraceId"]  = traceId,
            ["ClientIp"] = clientIp,
        });

        var request = new ChangePasswordRequest(
            Username:        model.Username,
            CurrentPassword: model.CurrentPassword,
            NewPassword:     model.NewPassword)
        {
            Recaptcha = model.Recaptcha,
        };

        var outcome = await _changeFlow.HandleAsync(request, new RequestContext(clientIp, traceId));

        if (outcome.Disposition != Disposition.Ok)
        {
            var result = new ApiResult();
            result.Errors.Add(outcome.Error!);   // already redacted by the flow's IErrorRedactor
            return BadRequest(result);
        }

        if (outcome.Notification is { } notify)
            FireNotification(notify);

        return Ok(new ApiResult(outcome.SuccessMessage));
    }

    /// <summary>
    /// Fire-and-forget password-changed email. The directory lookup + templating run OFF the
    /// HTTP response path (Task.Run) so SMTP/LDAP latency never blocks the response — identical
    /// to the pre-refactor behavior.
    /// </summary>
    private void FireNotification(NotificationRequest notify)
    {
        var emailSvc = _emailService;
        var notifCfg = _emailNotifSettings.Value;
        var directoryReader = _directoryReader;

        _ = Task.Run(async () =>
        {
            var emailAddress = directoryReader.GetUserEmail(notify.Username);
            if (string.IsNullOrWhiteSpace(emailAddress)) return;

            var body = notifCfg.BodyTemplate
                .Replace("{Username}",  notify.Username,  StringComparison.Ordinal)
                .Replace("{Timestamp}", notify.Timestamp, StringComparison.Ordinal)
                .Replace("{IpAddress}", notify.ClientIp,  StringComparison.Ordinal);

            await emailSvc.SendAsync(emailAddress, notify.Username, notifCfg.Subject, body);
        });
    }

    /// <summary>
    /// Status Check (v2.1): authenticate with the current password and return resolved
    /// expiry + live policy WITHOUT changing anything. Enumeration-safe: failures route
    /// through the same STAB-013 redaction as the change flow. Same reCAPTCHA gate.
    /// POST /api/password/status
    /// </summary>
    [HttpPost("status")]
    [EnableRateLimiting("status-fixed-window")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> StatusAsync([FromBody] StatusCheckModel model)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!ModelState.IsValid)
        {
            Audit("StatusValidationFailed", model.Username, clientIp, SiemEventType.ValidationFailed);
            return BadRequest(ApiResult.FromModelStateErrors(ModelState));
        }

        var settings = _clientSettings.Value;
        var recaptchaConfig = settings.Recaptcha;
        if (recaptchaConfig?.Enabled == true && !string.IsNullOrWhiteSpace(recaptchaConfig.PrivateKey))
        {
            if (!await _recaptchaVerifier.VerifyAsync(model.Recaptcha, "change_password", clientIp))
            {
                Audit("StatusRecaptchaFailed", model.Username, clientIp, SiemEventType.RecaptchaFailed);
                return BadRequest(ApiResult.InvalidCaptcha());
            }
        }

        var status = await _statusReader.GetUserPasswordStatusAsync(model.Username, model.CurrentPassword);

        if (!status.Authenticated)
        {
            var code = status.Error ?? ApiErrorCode.InvalidCredentials;
            Audit($"StatusFailed:{code}", model.Username, clientIp, MapErrorCodeToSiemEvent(code));
            var result = new ApiResult();
            result.Errors.Add(RedactIfProduction(new ApiErrorItem(code)));  // STAB-013 collapse
            return BadRequest(result);
        }

        Audit("StatusChecked", model.Username, clientIp, SiemEventType.StatusChecked);

        return Ok(new StatusResponse(
            Authenticated: true,
            ExpiresUtc:    status.ExpiresUtc?.UtcDateTime.ToString("o"),
            NeverExpires:  status.NeverExpires,
            Source:        status.Source.ToString(),
            Policy:        status.Policy));
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private void Audit(string outcome, string username, string clientIp,
        SiemEventType? siemEvent = null, string? detail = null)
    {
        _logger.LogInformation(
            "PasswordChange outcome={Outcome} user={User} ip={Ip}",
            outcome, username, clientIp);

        if (siemEvent.HasValue)
        {
            var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "unknown";
            _siemService.LogEvent(new AuditEvent(
                EventType: siemEvent.Value,
                Outcome:   outcome,
                Username:  username,
                ClientIp:  clientIp,
                TraceId:   traceId,
                Detail:    detail));
        }
    }

    private static SiemEventType MapErrorCodeToSiemEvent(ApiErrorCode code) => code switch
    {
        ApiErrorCode.InvalidCredentials  => SiemEventType.InvalidCredentials,
        ApiErrorCode.UserNotFound        => SiemEventType.UserNotFound,
        ApiErrorCode.PortalLockout       => SiemEventType.PortalLockout,
        ApiErrorCode.ApproachingLockout  => SiemEventType.ApproachingLockout,
        ApiErrorCode.ChangeNotPermitted  => SiemEventType.ChangeNotPermitted,
        _                                => SiemEventType.Generic,
    };

    /// <summary>
    /// STAB-013 D-01: account-enumeration codes that MUST collapse to Generic on the wire
    /// in Production. <see cref="ApiErrorCode.InvalidCredentials"/> and
    /// <see cref="ApiErrorCode.UserNotFound"/> leak whether a username exists in AD, so they
    /// are redacted. Deliberately EXCLUDED: <see cref="ApiErrorCode.ApproachingLockout"/> and
    /// <see cref="ApiErrorCode.PortalLockout"/> — these leak only per-account portal-throttling
    /// state (this portal is rate-limiting this account), never directory membership, so they
    /// are safe to expose and are NOT an enumeration vector. SIEM granularity is preserved
    /// independently (D-05); see GenericErrorMappingTests.Production_ApproachingLockout_*
    /// / Production_PortalLockout_* for the regression guard.
    /// </summary>
    private static bool IsAccountEnumerationCode(ApiErrorCode code) =>
        code is ApiErrorCode.InvalidCredentials or ApiErrorCode.UserNotFound;

    /// <summary>
    /// STAB-013: In Production, replace InvalidCredentials/UserNotFound with Generic (0)
    /// to resist account enumeration. SIEM path is NOT affected — granularity preserved
    /// upstream by MapErrorCodeToSiemEvent + Audit() call (see D-05).
    /// </summary>
    private ApiErrorItem RedactIfProduction(ApiErrorItem err) =>
        _hostEnvironment.IsProduction() && IsAccountEnumerationCode(err.ErrorCode)
            ? new ApiErrorItem(ApiErrorCode.Generic, err.Message)
            : err;

}
