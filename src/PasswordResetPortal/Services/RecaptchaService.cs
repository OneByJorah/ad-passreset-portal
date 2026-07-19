using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasswordResetPortal.Services;

/// <summary>
/// Google reCAPTCHA verification service.
/// Validates reCAPTCHA tokens from the frontend.
/// Ported from pypass project.
/// </summary>
public class RecaptchaService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RecaptchaService> _logger;

    public RecaptchaService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RecaptchaService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Verify a reCAPTCHA token with Google's API.
    /// </summary>
    /// <param name="token">The reCAPTCHA response token from the client.</param>
    /// <param name="remoteIp">The user's IP address (optional).</param>
    /// <returns>True if verification succeeded, false otherwise.</returns>
    public async Task<bool> VerifyAsync(string token, string? remoteIp = null)
    {
        // Check if reCAPTCHA is enabled
        var enabled = _configuration.GetValue<bool>("Recaptcha:Enabled", true);
        if (!enabled)
        {
            _logger.LogDebug("reCAPTCHA is disabled, skipping verification");
            return true;
        }

        var secretKey = _configuration["Recaptcha:SecretKey"];
        if (string.IsNullOrEmpty(secretKey))
        {
            _logger.LogWarning("reCAPTCHA secret key not configured");
            return false;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync(
                "https://www.google.com/recaptcha/api/siteverify",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "secret", secretKey },
                    { "response", token },
                    { "remoteip", remoteIp ?? string.Empty }
                }));

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<RecaptchaResponse>(json);

            return result?.Success == true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "reCAPTCHA verification failed");
            return false;
        }
    }
}

public class RecaptchaResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error-codes")]
    public string[] ErrorCodes { get; set; } = Array.Empty<string>();
}
