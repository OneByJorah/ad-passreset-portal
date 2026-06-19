using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PassReset.Common;
using PassReset.PasswordProvider;
using PassReset.Web.Helpers;
using PassReset.Web.Models;
using PassReset.Web.Services;

namespace PassReset.Tests.Windows.Web.Controllers;

/// <summary>
/// Integration tests for POST /api/password/status (v2.1 Status Check) via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> with the in-process
/// <c>DebugPasswordChangeProvider</c>. Magic usernames drive deterministic outcomes
/// (validUser → authenticated + resolved expiry; invalidCredentials → InvalidCredentials;
/// neverExpires → authenticated never-expires). The Production-environment test proves the
/// STAB-013 enumeration redaction also applies to this new endpoint (security regression guard).
/// </summary>
public class StatusEndpointTests
{
    // ── Request DTO mirrors StatusCheckModel's JSON contract ─────────────────────
    private sealed class StatusRequestDto
    {
        [JsonPropertyName("username")]        public string Username        { get; set; } = string.Empty;
        [JsonPropertyName("currentPassword")] public string CurrentPassword { get; set; } = string.Empty;
        [JsonPropertyName("recaptcha")]       public string Recaptcha       { get; set; } = string.Empty;
    }

    private static StatusRequestDto MakeRequest(string username) => new()
    {
        Username        = username,
        CurrentPassword = "OldPassword1!",
        Recaptcha       = string.Empty,
    };

    // ── Wire-shape DTOs (records expose getter-only props; STJ needs settable DTOs) ──
    private sealed class StatusResponseDto
    {
        [JsonPropertyName("authenticated")] public bool    Authenticated { get; set; }
        [JsonPropertyName("expiresUtc")]    public string? ExpiresUtc    { get; set; }
        [JsonPropertyName("neverExpires")]  public bool    NeverExpires  { get; set; }
        [JsonPropertyName("source")]        public string? Source        { get; set; }
        [JsonPropertyName("policy")]        public PolicyDto? Policy     { get; set; }
    }

    private sealed class PolicyDto
    {
        [JsonPropertyName("minLength")]          public int  MinLength          { get; set; }
        [JsonPropertyName("requiresComplexity")] public bool RequiresComplexity { get; set; }
        [JsonPropertyName("historyLength")]      public int  HistoryLength      { get; set; }
        [JsonPropertyName("minAgeDays")]         public int  MinAgeDays         { get; set; }
        [JsonPropertyName("maxAgeDays")]         public int  MaxAgeDays         { get; set; }
    }

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

    private static async Task<StatusResponseDto?> ReadStatusAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<StatusResponseDto>();

    private static async Task<ApiResultDto?> ReadResultAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ApiResultDto>();

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // ── Shared test config + service swaps (mirrors GenericErrorMappingTests) ─────
    private static Dictionary<string, string?> TestConfig() => new()
    {
        // UseDebugProvider must be false in Production (Program.cs guard); we inject the
        // debug provider via ConfigureTestServices instead so the env stays Production.
        ["WebSettings:UseDebugProvider"]                 = "false",
        ["WebSettings:EnableHttpsRedirect"]              = "false",
        ["ClientSettings:MinimumDistance"]               = "0",
        ["ClientSettings:Recaptcha:Enabled"]             = "false",
        ["EmailNotificationSettings:Enabled"]            = "false",
        ["PasswordExpiryNotificationSettings:Enabled"]   = "false",
        ["SiemSettings:Syslog:Enabled"]                  = "false",
        ["SiemSettings:AlertEmail:Enabled"]              = "false",
        ["PasswordChangeOptions:PortalLockoutThreshold"] = "0",
        ["PasswordChangeOptions:UseAutomaticContext"]    = "true",
    };

    /// <summary>
    /// Swap the AD-bound provider chain for the debug provider so magic usernames produce
    /// deterministic status outcomes without touching AD. Required because Program.cs refuses
    /// to boot with UseDebugProvider=true outside Development.
    /// </summary>
    private static void SwapInDebugProvider(IServiceCollection services)
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(IPasswordChangeProvider) ||
            d.ServiceType == typeof(LockoutPasswordChangeProvider) ||
            d.ServiceType == typeof(PasswordChangeProvider) ||
            d.ServiceType == typeof(ILockoutDiagnostics) ||
            d.ServiceType == typeof(IEmailService)).ToList();
        foreach (var d in descriptors) services.Remove(d);

        services.AddSingleton<DebugPasswordChangeProvider>();
        services.AddSingleton<LockoutPasswordChangeProvider>(sp =>
            new LockoutPasswordChangeProvider(
                sp.GetRequiredService<DebugPasswordChangeProvider>(),
                sp.GetRequiredService<IOptions<PasswordChangeOptions>>(),
                sp.GetRequiredService<ILogger<LockoutPasswordChangeProvider>>()));
        services.AddSingleton<IPasswordChangeProvider>(sp =>
            sp.GetRequiredService<LockoutPasswordChangeProvider>());
        services.AddSingleton<ILockoutDiagnostics>(sp =>
            sp.GetRequiredService<LockoutPasswordChangeProvider>());
        services.AddSingleton<IEmailService, NoOpEmailService>();
    }

    /// <summary>Development env — granular error codes reach the wire (no STAB-013 collapse).</summary>
    private sealed class DevelopmentEnvFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(TestConfig()));
            builder.ConfigureTestServices(SwapInDebugProvider);
        }
    }

    /// <summary>Production env — STAB-013 collapse gate fires (InvalidCredentials → Generic).</summary>
    private sealed class ProductionEnvFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production"); // CRITICAL for STAB-013 gate
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(TestConfig()));
            builder.ConfigureTestServices(SwapInDebugProvider);
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_ValidUser_Returns200_AuthenticatedWithExpiryAndPolicy()
    {
        using var factory = new DevelopmentEnvFactory();
        using var client  = NewClient(factory);

        var response = await client.PostAsJsonAsync("/api/password/status", MakeRequest("validUser"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadStatusAsync(response);
        Assert.NotNull(body);
        Assert.True(body!.Authenticated);
        Assert.NotNull(body.ExpiresUtc);
        Assert.False(body.NeverExpires);
        Assert.NotNull(body.Policy);
    }

    /// <summary>
    /// STAB-013 security regression guard: a non-authenticated status check in PRODUCTION must
    /// collapse InvalidCredentials (account-enumeration oracle) to Generic (0) on the wire.
    /// Do NOT weaken this test — it proves the new endpoint inherits the enumeration redaction.
    /// </summary>
    [Fact]
    public async Task Status_InvalidCredentials_InProduction_WireReturnsGeneric()
    {
        using var factory = new ProductionEnvFactory();
        using var client  = NewClient(factory);

        var response = await client.PostAsJsonAsync("/api/password/status", MakeRequest("invalidCredentials"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResultAsync(response);
        Assert.NotNull(result);
        Assert.Single(result!.Errors);
        Assert.Equal(ApiErrorCode.Generic, result.Errors[0].ErrorCode);
        Assert.NotEqual(ApiErrorCode.InvalidCredentials, result.Errors[0].ErrorCode);
    }

    [Fact]
    public async Task Status_NeverExpires_Returns200_NeverExpiresTrue_NoExpiry()
    {
        using var factory = new DevelopmentEnvFactory();
        using var client  = NewClient(factory);

        var response = await client.PostAsJsonAsync("/api/password/status", MakeRequest("neverExpires"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadStatusAsync(response);
        Assert.NotNull(body);
        Assert.True(body!.Authenticated);
        Assert.True(body.NeverExpires);
        Assert.Null(body.ExpiresUtc);
    }

    [Fact]
    public async Task Status_MissingUsername_Returns400()
    {
        using var factory = new DevelopmentEnvFactory();
        using var client  = NewClient(factory);

        // Username omitted → ModelState [Required] fails before the provider is called.
        var response = await client.PostAsJsonAsync("/api/password/status", new StatusRequestDto
        {
            Username        = string.Empty,
            CurrentPassword = "OldPassword1!",
            Recaptcha       = string.Empty,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Mirror the existing Post_MissingRequiredFields assertion style (raw body) — ModelState
        // validation errors carry the FieldRequired code name in the response.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(ApiErrorCode.FieldRequired), raw, StringComparison.Ordinal);
    }
}
