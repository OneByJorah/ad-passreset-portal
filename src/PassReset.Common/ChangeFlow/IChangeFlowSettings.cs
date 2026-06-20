namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The flat subset of configuration the Change Flow needs, decoupled from the Web
/// ClientSettings shape. The controller/DI adapts IOptions&lt;ClientSettings&gt; to this.
/// </summary>
public interface IChangeFlowSettings
{
    /// <summary>Minimum Levenshtein distance between old and new password (0 disables the check).</summary>
    int MinimumDistance { get; }

    /// <summary>True when reCAPTCHA verification should run (enabled AND a private key is configured).</summary>
    bool RecaptchaEnabled { get; }

    /// <summary>The expected reCAPTCHA action (e.g. "change_password").</summary>
    string RecaptchaAction { get; }

    /// <summary>True when a password-changed notification should be requested on success.</summary>
    bool NotificationEnabled { get; }
}
