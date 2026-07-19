using System.Net.Http;
using System.Text.Json;

namespace PasswordResetPortal.Services;

/// <summary>
/// Slack notification service for password change events.
/// Ported from pypass project.
/// </summary>
public class SlackNotificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SlackNotificationService> _logger;

    public SlackNotificationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SlackNotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Send a notification to Slack when a password is changed.
    /// </summary>
    public async Task NotifyPasswordChangeAsync(string username, bool success, string? errorMessage = null)
    {
        var enabled = _configuration.GetValue<bool>("Slack:Enabled", false);
        if (!enabled) return;

        var webhookUrl = _configuration["Slack:WebhookUrl"];
        if (string.IsNullOrEmpty(webhookUrl))
        {
            _logger.LogWarning("Slack webhook URL not configured");
            return;
        }

        var status = success ? "✅ Success" : "❌ Failed";
        var message = success
            ? $"Password changed successfully for *{username}*"
            : $"Password change failed for *{username}*\nReason: {errorMessage ?? "Unknown error"}";

        var payload = new
        {
            text = message,
            blocks = new object[]
            {
                new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = message }
                },
                new
                {
                    type = "context",
                    elements = new object[]
                    {
                        new { type = "mrkdwn", text = $"Status: {status} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC" }
                    }
                }
            }
        };

        try
        {
            var client = _httpClientFactory.CreateClient();
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync(webhookUrl, content);
            _logger.LogInformation("Slack notification sent for user {Username}", username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Slack notification for user {Username}", username);
        }
    }
}
