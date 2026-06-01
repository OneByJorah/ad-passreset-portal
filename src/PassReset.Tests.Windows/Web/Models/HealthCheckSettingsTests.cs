using Microsoft.Extensions.Options;
using PassReset.Web.Models;

namespace PassReset.Tests.Windows.Web.Models;

public class HealthCheckSettingsTests
{
    [Fact]
    public void Defaults_AllProbesEnabled_GraceIs600Seconds()
    {
        var s = new HealthCheckSettings();

        Assert.False(s.DisableSmtpConnectivityProbe);
        Assert.False(s.DisableExpiryServiceCheck);
        Assert.False(s.DisableAdConnectivityProbe);
        Assert.Equal(600, s.ExpiryServiceGracePeriodSeconds);
    }

    [Fact]
    public void Validator_NegativeGracePeriod_Fails()
    {
        var v = new HealthCheckSettingsValidator();
        var result = v.Validate(null, new HealthCheckSettings { ExpiryServiceGracePeriodSeconds = -1 });

        Assert.True(result.Failed);
        Assert.Contains("ExpiryServiceGracePeriodSeconds", string.Join(";", result.Failures!));
    }

    [Fact]
    public void Validator_ValidSettings_Succeeds()
    {
        var v = new HealthCheckSettingsValidator();
        var result = v.Validate(null, new HealthCheckSettings { ExpiryServiceGracePeriodSeconds = 600 });

        Assert.True(result.Succeeded);
    }
}
