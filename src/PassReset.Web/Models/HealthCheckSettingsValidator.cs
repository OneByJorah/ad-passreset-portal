using Microsoft.Extensions.Options;

namespace PassReset.Web.Models;

/// <summary>
/// Validates <see cref="HealthCheckSettings"/> at application startup. The grace
/// period must be non-negative; a negative value would make every not-yet-run
/// expiry service appear stuck on the first request.
/// </summary>
public sealed class HealthCheckSettingsValidator : IValidateOptions<HealthCheckSettings>
{
    private static string Fmt(string path, string reason, string actual)
        => $"{path}: {reason} (got \"{actual}\"). Edit appsettings.Production.json or run Install-PassReset.ps1 -Reconfigure.";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HealthCheckSettings options)
    {
        if (options.ExpiryServiceGracePeriodSeconds < 0)
            return ValidateOptionsResult.Fail(Fmt(
                "HealthCheckSettings.ExpiryServiceGracePeriodSeconds",
                "must be >= 0",
                options.ExpiryServiceGracePeriodSeconds.ToString()));

        return ValidateOptionsResult.Success;
    }
}
