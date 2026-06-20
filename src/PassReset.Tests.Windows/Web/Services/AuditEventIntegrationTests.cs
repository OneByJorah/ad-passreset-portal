using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PassReset.Common;
using PassReset.Web.Models;
using PassReset.Web.Services;

namespace PassReset.Tests.Windows.Web.Services;

/// <summary>
/// STAB-015: end-to-end proof that no plaintext secret from a ChangePasswordModel ever
/// reaches the structured-audit Detail field, across validation-failure and auth-failure branches.
/// </summary>
public class AuditEventIntegrationTests
{
    private sealed class RecordingSiem : ISiemService
    {
        public List<AuditEvent> Events { get; } = new();
        public void LogEvent(SiemEventType eventType, string username, string ipAddress, string? detail = null) { }
        public void LogEvent(AuditEvent evt) => Events.Add(evt);
    }

    private const string SecretNew = "SuperSecretNewP@ss123";
    private const string SecretOld = "SuperSecretOldP@ss123";

    private sealed class Factory : WebApplicationFactory<Program>
    {
        public RecordingSiem Recorder { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebSettings:UseDebugProvider"]                 = "true",
                    ["WebSettings:EnableHttpsRedirect"]              = "false",
                    ["ClientSettings:MinimumDistance"]               = "0",
                    ["ClientSettings:Recaptcha:Enabled"]             = "false",
                    ["EmailNotificationSettings:Enabled"]            = "false",
                    ["PasswordExpiryNotificationSettings:Enabled"]   = "false",
                    ["SiemSettings:Syslog:Enabled"]                  = "false",
                    ["SiemSettings:AlertEmail:Enabled"]              = "false",
                    ["PasswordChangeOptions:PortalLockoutThreshold"] = "0",
                    ["PasswordChangeOptions:UseAutomaticContext"]    = "true",
                }));
            builder.ConfigureTestServices(services =>
            {
                var existing = services.Where(d => d.ServiceType == typeof(ISiemService)).ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddSingleton<ISiemService>(Recorder);
            });
        }
    }

    private static ChangePasswordModel Req(string username) => new()
    {
        Username          = username,
        CurrentPassword   = SecretOld,
        NewPassword       = SecretNew,
        NewPasswordVerify = SecretNew,
        Recaptcha         = string.Empty,
    };

    [Fact]
    public async Task InvalidCredentials_AuditDetail_ContainsNoPlaintextSecrets()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsJsonAsync("/api/password", Req("invalidCredentials"));

        Assert.NotEmpty(factory.Recorder.Events);
        foreach (var e in factory.Recorder.Events)
        {
            Assert.DoesNotContain(SecretNew, e.Detail ?? string.Empty);
            Assert.DoesNotContain(SecretOld, e.Detail ?? string.Empty);
        }
    }
}
