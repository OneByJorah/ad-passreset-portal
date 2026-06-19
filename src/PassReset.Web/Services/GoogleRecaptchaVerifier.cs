using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PassReset.Web.Models;

namespace PassReset.Web.Services;

/// <summary>
/// Verifies reCAPTCHA v3 tokens against Google's siteverify endpoint. Registered as a typed
/// HttpClient with BaseAddress https://www.google.com/. Honors
/// <see cref="Recaptcha.FailOpenOnUnavailable"/> for service outages but never fails open on
/// an unexpected/parse error.
/// </summary>
public sealed class GoogleRecaptchaVerifier : IRecaptchaVerifier
{
    private readonly HttpClient _http;
    private readonly IOptions<ClientSettings> _clientSettings;
    private readonly ILogger<GoogleRecaptchaVerifier> _logger;

    public GoogleRecaptchaVerifier(
        HttpClient http,
        IOptions<ClientSettings> clientSettings,
        ILogger<GoogleRecaptchaVerifier> logger)
    {
        _http = http;
        _clientSettings = clientSettings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string token, string action, string clientIp)
    {
        var config = _clientSettings.Value.Recaptcha;

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"]   = config.PrivateKey!,
                ["response"] = token,
                ["remoteip"] = clientIp,
            });

            var response = await _http.PostAsync("recaptcha/api/siteverify", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("reCAPTCHA API returned {StatusCode} for IP {ClientIp}",
                    response.StatusCode, clientIp);
                if (config.FailOpenOnUnavailable)
                {
                    _logger.LogWarning("reCAPTCHA fail-open enabled — allowing request through for IP {ClientIp}", clientIp);
                    return true;
                }
                return false;
            }

            var json = await response.Content.ReadFromJsonAsync<RecaptchaResponse>();
            return json?.Success == true
                && json.Score >= config.ScoreThreshold
                && string.Equals(json.Action, action, StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "reCAPTCHA service unreachable for IP {ClientIp}", clientIp);
            if (config.FailOpenOnUnavailable)
            {
                _logger.LogWarning("reCAPTCHA fail-open enabled — allowing request through for IP {ClientIp}", clientIp);
                return true;
            }
            return false;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "reCAPTCHA request timed out for IP {ClientIp}", clientIp);
            if (config.FailOpenOnUnavailable)
            {
                _logger.LogWarning("reCAPTCHA fail-open enabled — allowing request through for IP {ClientIp}", clientIp);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected errors (JSON parse, etc.) — never fail-open
            _logger.LogWarning(ex, "reCAPTCHA unexpected error for IP {ClientIp}", clientIp);
            return false;
        }
    }

    // Minimal DTO for reCAPTCHA v3 API response deserialization
    private sealed class RecaptchaResponse
    {
        public bool  Success { get; set; }
        public float Score   { get; set; }
        public string? Action { get; set; }
    }
}
