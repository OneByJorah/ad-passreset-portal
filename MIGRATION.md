# Migration Notice

## Consolidated from pypass

This repository has been consolidated with **pypass** (previously a separate Active Directory password reset portal). All unique features from pypass have been merged into ad-passreset-portal:

### Features Merged

| Feature | Source | Description |
|---------|--------|-------------|
| reCAPTCHA Support | pypass | Google reCAPTCHA verification with enable/disable toggle |
| Slack Notifications | pypass | Password change notifications via Slack webhook |

### Configuration

Add these settings to `appsettings.json`:

```json
{
  "Recaptcha": {
    "Enabled": true,
    "SiteKey": "your-site-key",
    "SecretKey": "your-secret-key"
  },
  "Slack": {
    "Enabled": false,
    "WebhookUrl": "https://hooks.slack.com/services/xxx"
  }
}
```

### Services Added

- `RecaptchaService.cs` - Google reCAPTCHA verification
- `SlackNotificationService.cs` - Slack webhook notifications

### Deprecated Repository

The `pypass` repository is now deprecated. All development continues in this repository.
