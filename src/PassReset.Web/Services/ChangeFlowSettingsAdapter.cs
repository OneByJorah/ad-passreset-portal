using Microsoft.Extensions.Options;
using PassReset.Common.ChangeFlow;
using PassReset.Web.Models;

namespace PassReset.Web.Services;

/// <summary>
/// Adapts the Web ClientSettings shape to the flat <see cref="IChangeFlowSettings"/> the
/// Change Flow consumes. Reads IOptions live so config reloads are honored per request.
/// </summary>
public sealed class ChangeFlowSettingsAdapter(
    IOptions<ClientSettings> clientSettings,
    IOptions<EmailNotificationSettings> emailNotifSettings) : IChangeFlowSettings
{
    private readonly IOptions<ClientSettings> _clientSettings = clientSettings;
    private readonly IOptions<EmailNotificationSettings> _emailNotifSettings = emailNotifSettings;

    public int MinimumDistance => _clientSettings.Value.MinimumDistance;

    public bool RecaptchaEnabled
    {
        get
        {
            var r = _clientSettings.Value.Recaptcha;
            return r?.Enabled == true && !string.IsNullOrWhiteSpace(r.PrivateKey);
        }
    }

    public string RecaptchaAction => "change_password";

    public bool NotificationEnabled => _emailNotifSettings.Value.Enabled;
}
