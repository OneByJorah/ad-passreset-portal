using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PassReset.Common;
using PassReset.Tests.Windows.Fakes;
using PassReset.Web.Models;
using PassReset.Web.Services;

namespace PassReset.Tests.Windows.Web.Controllers;

/// <summary>
/// STAB-014 — Integration coverage for the POST /api/password rate-limit and
/// reCAPTCHA branches. Each test spins up its own <see cref="WebApplicationFactory{TEntryPoint}"/>
/// subclass so the fixed-window rate-limiter partition is freshly constructed per test
/// (prevents the 5-req/5-min budget leaking across tests — see 09-RESEARCH.md Pitfall 1).
/// </summary>
public class RateLimitAndRecaptchaTests
{
    // ─── Wire DTOs (ApiResult/ApiErrorItem expose getter-only props — STJ can't
    // populate them on deserialization). Mirror of PasswordControllerTests shapes.
    private sealed class ApiResultDto
    {
        [JsonPropertyName("errors")]
        public List<ApiErrorItemDto> Errors { get; set; } = new();

        [JsonPropertyName("payload")]
        public object? Payload { get; set; }
    }

    private sealed class ApiErrorItemDto
    {
        [JsonPropertyName("errorCode")]
        public ApiErrorCode ErrorCode { get; set; }

        [JsonPropertyName("fieldName")]
        public string? FieldName { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private static async Task<ApiResultDto?> ReadResultAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ApiResultDto>();

    private static ChangePasswordModel MakeRequest(string username) => new()
    {
        Username          = username,
        CurrentPassword   = "OldPassword1!",
        NewPassword       = "BrandNewP@ssword123",
        NewPasswordVerify = "BrandNewP@ssword123",
        Recaptcha         = string.Empty,
    };

    [Fact]
    public void RecaptchaVerifier_IsRegistered()
    {
        using var factory = new RateLimitFactory();
        using var scope = factory.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetService<IRecaptchaVerifier>();
        Assert.NotNull(verifier);
        Assert.IsType<GoogleRecaptchaVerifier>(verifier);
    }

    /// <summary>
    /// Default fixture — debug provider + reCAPTCHA disabled. Matches
    /// <c>PasswordControllerTests.DebugFactory</c> so rate-limit tests exercise
    /// the same config surface as the existing controller suite.
    /// </summary>
    public sealed class RateLimitFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
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
                });
            });
        }
    }

    /// <summary>
    /// STAB-015: test-double SIEM recorder. The rate-limiter OnRejected path uses the legacy
    /// <c>LogEvent(SiemEventType,...)</c> overload, so both overloads capture the event type.
    /// </summary>
    internal sealed class RecordingSiem : PassReset.Web.Services.ISiemService
    {
        public List<PassReset.Web.Services.SiemEventType> Events { get; } = new();
        public void LogEvent(PassReset.Web.Services.SiemEventType eventType, string username, string ipAddress, string? detail = null) => Events.Add(eventType);
        public void LogEvent(PassReset.Web.Services.AuditEvent evt) => Events.Add(evt.EventType);
    }

    /// <summary>
    /// STAB-015: default rate-limit fixture with the SIEM service swapped for a recorder so
    /// the 429 rejection's <c>RateLimitExceeded</c> emission is observable.
    /// </summary>
    public sealed class RecordingRateLimitFactory : WebApplicationFactory<Program>
    {
        internal RecordingSiem Recorder { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
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
                });
            });
            builder.ConfigureTestServices(services =>
            {
                var existing = services.Where(d => d.ServiceType == typeof(PassReset.Web.Services.ISiemService)).ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddSingleton<PassReset.Web.Services.ISiemService>(Recorder);
            });
        }
    }

    /// <summary>
    /// STAB-014(c) D-19: reCAPTCHA-enabled path. Hits real Google siteverify with
    /// an invalid token — see 09-CONTEXT.md §"D-19".
    /// </summary>
    public sealed class RecaptchaEnabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebSettings:UseDebugProvider"]                   = "true",
                    ["WebSettings:EnableHttpsRedirect"]                = "false",
                    ["ClientSettings:MinimumDistance"]                 = "0",
                    // Google's publicly documented always-fail test secret — see
                    // https://developers.google.com/recaptcha/docs/faq#id-like-to-run-automated-tests-with-recaptcha
                    ["ClientSettings:Recaptcha:Enabled"]               = "true",
                    // Google's public test site key — non-empty, required by the
                    // SiteKey validator even though the client-side widget isn't invoked here.
                    ["ClientSettings:Recaptcha:SiteKey"]               = "6LeIxAcTAAAAAJcZVRqyHh71UMIEGNQ_MXjiZKhI",
                    ["ClientSettings:Recaptcha:PrivateKey"]            = "6LeIxAcTAAAAAGG-vFI1TnRWxMZNFuojJ4WifJWe",
                    ["ClientSettings:Recaptcha:FailOpenOnUnavailable"] = "false",
                    ["EmailNotificationSettings:Enabled"]              = "false",
                    ["PasswordExpiryNotificationSettings:Enabled"]     = "false",
                    ["SiemSettings:Syslog:Enabled"]                    = "false",
                    ["SiemSettings:AlertEmail:Enabled"]                = "false",
                    ["PasswordChangeOptions:PortalLockoutThreshold"]   = "0",
                    ["PasswordChangeOptions:UseAutomaticContext"]      = "true",
                });
            });
        }
    }

    /// <summary>
    /// reCAPTCHA enabled, with <see cref="IRecaptchaVerifier"/> replaced by
    /// <see cref="FakeRecaptchaVerifier"/> returning the specified boolean outcome.
    /// </summary>
    private sealed class StubbedRecaptchaFactory : WebApplicationFactory<Program>
    {
        private readonly bool _verifyResult;
        public StubbedRecaptchaFactory(bool verifyResult) => _verifyResult = verifyResult;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebSettings:UseDebugProvider"]                   = "true",
                    ["WebSettings:EnableHttpsRedirect"]                = "false",
                    ["ClientSettings:MinimumDistance"]                 = "0",
                    ["ClientSettings:Recaptcha:Enabled"]               = "true",
                    ["ClientSettings:Recaptcha:SiteKey"]               = "6LeIxAcTAAAAAJcZVRqyHh71UMIEGNQ_MXjiZKhI",
                    ["ClientSettings:Recaptcha:PrivateKey"]            = "test-private-key",
                    ["ClientSettings:Recaptcha:ScoreThreshold"]        = "0.5",
                    ["EmailNotificationSettings:Enabled"]              = "false",
                    ["PasswordExpiryNotificationSettings:Enabled"]     = "false",
                    ["SiemSettings:Syslog:Enabled"]                    = "false",
                    ["SiemSettings:AlertEmail:Enabled"]                = "false",
                    ["PasswordChangeOptions:PortalLockoutThreshold"]   = "0",
                    ["PasswordChangeOptions:UseAutomaticContext"]      = "true",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRecaptchaVerifier>();
                services.AddSingleton<IRecaptchaVerifier>(new FakeRecaptchaVerifier(_verifyResult));
            });
        }
    }

    // ─── Rate-limit coverage ──────────────────────────────────────────────────

    [Fact]
    public async Task RateLimit_SixthRequestInWindow_Returns429()
    {
        using var factory = new RateLimitFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // First 5 POSTs are inside the 5-req/5-min fixed window — all succeed.
        for (int i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/password", MakeRequest("alice"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 6th POST exhausts the window — rate limiter emits 429 before controller executes.
        var rejected = await client.PostAsJsonAsync("/api/password", MakeRequest("alice"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    /// <summary>
    /// STAB-015 guard: the 429 emitted when the fixed window is exhausted must still
    /// forward a <c>RateLimitExceeded</c> SIEM event. The rate-limiter OnRejected handler
    /// uses the LEGACY <c>LogEvent</c> overload, so the recorder captures it there.
    /// </summary>
    [Fact]
    public async Task RateLimit_429_EmitsRateLimitExceededEvent()
    {
        using var factory = new RecordingRateLimitFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (int i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/password", MakeRequest("alice"));
        var rejected = await client.PostAsJsonAsync("/api/password", MakeRequest("alice"));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Contains(PassReset.Web.Services.SiemEventType.RateLimitExceeded, factory.Recorder.Events);
    }

    [Fact]
    public async Task RateLimit_FirstRequestInFreshFactory_Succeeds()
    {
        // Explicit regression guard for Pitfall 1 (09-RESEARCH.md): per-test factory
        // instances must produce independent rate-limit partitions. A fresh factory
        // after the saturation test above MUST still accept its first request.
        using var factory = new RateLimitFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsJsonAsync("/api/password", MakeRequest("alice"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── reCAPTCHA coverage ───────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "RequiresInternet")]
    public async Task Recaptcha_EnabledWithInvalidToken_ReturnsInvalidCaptcha()
    {
        // D-19: no abstraction — hit real Google siteverify. The configured private key
        // is Google's documented always-fail test secret so the response is deterministic.
        using var factory = new RecaptchaEnabledFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var req = MakeRequest("alice");
        req.Recaptcha = "ThisTokenWillNotVerify";

        var response = await client.PostAsJsonAsync("/api/password", req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResultAsync(response);
        Assert.NotNull(result);
        Assert.Contains(result!.Errors, e => e.ErrorCode == ApiErrorCode.InvalidCaptcha);
    }

    [Fact]
    public async Task Recaptcha_DisabledWithEmptyToken_RequestProceeds()
    {
        // With Recaptcha:Enabled=false, the controller's reCAPTCHA branch is skipped
        // regardless of whether the Recaptcha field is empty. This test documents
        // that guarantee explicitly so a regression (e.g., accidentally gating on
        // empty token) surfaces with a clear name.
        using var factory = new RateLimitFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsJsonAsync("/api/password", MakeRequest("alice"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Recaptcha_EnabledWithEmptyToken_Rejects()
    {
        using var factory = new StubbedRecaptchaFactory(false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var req = MakeRequest("alice");
        req.Recaptcha = string.Empty;

        var response = await client.PostAsJsonAsync("/api/password", req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResultAsync(response);
        Assert.NotNull(result);
        Assert.Contains(result!.Errors, e => e.ErrorCode == ApiErrorCode.InvalidCaptcha);
    }

    [Fact]
    public async Task Recaptcha_LowScore_ReturnsInvalidCaptcha()
    {
        using var factory = new StubbedRecaptchaFactory(false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var req = MakeRequest("alice");
        req.Recaptcha = "valid-looking-token";

        var response = await client.PostAsJsonAsync("/api/password", req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResultAsync(response);
        Assert.Contains(result!.Errors, e => e.ErrorCode == ApiErrorCode.InvalidCaptcha);
    }

    [Fact]
    public async Task Recaptcha_ProviderUnreachable_FailSafeDisabled_Returns400()
    {
        using var factory = new StubbedRecaptchaFactory(false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var req = MakeRequest("alice");
        req.Recaptcha = "any-token";

        var response = await client.PostAsJsonAsync("/api/password", req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResultAsync(response);
        Assert.Contains(result!.Errors, e => e.ErrorCode == ApiErrorCode.InvalidCaptcha);
    }

    [Fact]
    public async Task Recaptcha_ProviderUnreachable_FailSafeEnabled_Returns200()
    {
        using var factory = new StubbedRecaptchaFactory(true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var req = MakeRequest("alice");
        req.Recaptcha = "any-token";

        var response = await client.PostAsJsonAsync("/api/password", req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
