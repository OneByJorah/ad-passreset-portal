# Extract an IRecaptchaVerifier Seam Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lift the reCAPTCHA verification logic (HttpClient round-trip, score/action check, fail-open) out of `PasswordController` behind an `IRecaptchaVerifier` seam, so it is tested once at unit level and exercised in controller tests through an in-memory fake instead of HTTP-handler mocking.

**Architecture:** A new deep service seam `IRecaptchaVerifier.VerifyAsync(token, action, clientIp) → Task<bool>` in `PassReset.Web/Services`, mirroring the existing `IEmailService`/`ISiemService` pattern. Production adapter `GoogleRecaptchaVerifier` is registered as a typed HttpClient (`AddHttpClient<IRecaptchaVerifier, GoogleRecaptchaVerifier>`), absorbing the named `"recaptcha"` client. The controller keeps the enable-gate, SIEM `RecaptchaFailed` audit, and `InvalidCaptcha()` response — only the Google round-trip and pass/fail decision move behind the seam. A test fake `FakeRecaptchaVerifier` is the second adapter that justifies the seam and replaces today's `HttpMessageHandler` stubbing in controller tests.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows` for Web + Tests.Windows), ASP.NET Core typed HttpClient, xUnit v3, `WebApplicationFactory<Program>` integration tests.

## Global Constraints

- **Behavior-preserving for the verification logic.** The pass/fail decision must be identical to today's `ValidateRecaptchaAsync`: pass iff `success == true && score >= ScoreThreshold && action == expectedAction (OrdinalIgnoreCase)`. Fail-open (`FailOpenOnUnavailable`) on non-2xx responses, `HttpRequestException`, and `TaskCanceledException`; **never** fail-open on unexpected/parse errors (return false).
- **Action is a parameter, not hardcoded.** `VerifyAsync(string token, string action, string clientIp)`. Both callers pass `"change_password"` today (preserves current behavior exactly — the frontend sends `change_password` for both change and status). Do NOT change the status action.
- **Controller keeps:** the enable-gate `recaptchaConfig?.Enabled == true && !string.IsNullOrWhiteSpace(recaptchaConfig.PrivateKey)`, the `Audit("RecaptchaFailed", …, SiemEventType.RecaptchaFailed)` call, and the `return BadRequest(ApiResult.InvalidCaptcha())` response. These do NOT move behind the seam.
- **Controller drops** `IHttpClientFactory httpClientFactory` and the `HttpClient _recaptchaHttp` field (its only HttpClient use is reCAPTCHA — verified). The private nested `RecaptchaResponse` DTO moves into `GoogleRecaptchaVerifier`.
- **Typed client registration:** replace `AddHttpClient("recaptcha", c => { c.BaseAddress = new Uri("https://www.google.com/"); c.Timeout = TimeSpan.FromSeconds(10); })` (Program.cs:138–142) with `AddHttpClient<IRecaptchaVerifier, GoogleRecaptchaVerifier>(c => { same BaseAddress + Timeout })`. The reCAPTCHA verify POST path is `"recaptcha/api/siteverify"` (relative to the `https://www.google.com/` base).
- **Config source:** the verifier reads `IOptions<ClientSettings>` and uses `.Value.Recaptcha` (`PrivateKey`, `ScoreThreshold`, `FailOpenOnUnavailable`). `Recaptcha` is in `PassReset.Web.Models` (namespace `PassReset.Web.Models`).
- **Location:** `IRecaptchaVerifier` + `GoogleRecaptchaVerifier` in `src/PassReset.Web/Services/`, namespace `PassReset.Web.Services`. `FakeRecaptchaVerifier` in `src/PassReset.Tests.Windows/Fakes/`, namespace `PassReset.Tests.Windows.Fakes`.
- **Tests:** TDD the Google adapter's 7 branches against the existing `FakeHttpMessageHandler` (`PassReset.Tests.Windows.Fakes`). Migrate controller reCAPTCHA outcome tests to `FakeRecaptchaVerifier`. Leave the real-Google `RecaptchaEnabledFactory` test (RateLimitAndRecaptchaTests.cs ~line 148) untouched.
- **Build/test:** `dotnet build src/PassReset.sln -c Release`; full suite `dotnet test src/PassReset.sln -c Release`. Three test projects exist (`PassReset.Tests`, `PassReset.Tests.Windows`, `PassReset.Tests.Integration.Ldap`); the reCAPTCHA tests are all in `PassReset.Tests.Windows`. Pre-existing warnings to ignore: ASP0000 (Program.cs:203), xUnit1051 (CancellationToken) in Tests.Windows.
- **Commit convention:** `type(scope): subject` — scope `web` for the seam/controller/DI, `test` for test migration.

---

## File Structure

**Create:**
- `src/PassReset.Web/Services/IRecaptchaVerifier.cs` — the seam: `Task<bool> VerifyAsync(string token, string action, string clientIp)`.
- `src/PassReset.Web/Services/GoogleRecaptchaVerifier.cs` — production adapter: typed HttpClient, score/action check, fail-open; private `RecaptchaResponse` DTO.
- `src/PassReset.Tests.Windows/Fakes/FakeRecaptchaVerifier.cs` — in-memory adapter for controller tests.
- `src/PassReset.Tests.Windows/Web/Services/GoogleRecaptchaVerifierTests.cs` — 7-branch unit tests via `FakeHttpMessageHandler`.

**Modify:**
- `src/PassReset.Web/Controllers/PasswordController.cs` — inject `IRecaptchaVerifier`; drop `IHttpClientFactory` + `_recaptchaHttp` + `ValidateRecaptchaAsync` + `RecaptchaResponse`; both call sites call `_recaptchaVerifier.VerifyAsync(model.Recaptcha, "change_password", clientIp)`.
- `src/PassReset.Web/Program.cs:138-142` — swap named `"recaptcha"` client for typed `AddHttpClient<IRecaptchaVerifier, GoogleRecaptchaVerifier>`.
- `src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs` — migrate `StubbedRecaptchaFactory` (and the outcome tests using it) to register `FakeRecaptchaVerifier`; keep `RecaptchaEnabledFactory` untouched; the `Recaptcha_NamedHttpClient_IsRegistered` test must be updated or removed (the named client no longer exists — see Task 4).

---

### Task 1: Define `IRecaptchaVerifier` and the `FakeRecaptchaVerifier` test adapter

Introduce the seam interface and the in-memory adapter first, so later tasks (controller, tests) have a stable contract to compile against.

**Files:**
- Create: `src/PassReset.Web/Services/IRecaptchaVerifier.cs`
- Create: `src/PassReset.Tests.Windows/Fakes/FakeRecaptchaVerifier.cs`

**Interfaces:**
- Produces: `IRecaptchaVerifier.VerifyAsync(string token, string action, string clientIp) → Task<bool>` (namespace `PassReset.Web.Services`). `FakeRecaptchaVerifier` (namespace `PassReset.Tests.Windows.Fakes`) with a constructor `FakeRecaptchaVerifier(bool result)` and a property `List<(string Token, string Action, string ClientIp)> Calls { get; }`.

- [ ] **Step 1: Create the seam interface**

Create `src/PassReset.Web/Services/IRecaptchaVerifier.cs`:

```csharp
namespace PassReset.Web.Services;

/// <summary>
/// Verifies a reCAPTCHA v3 token against the configured provider. Returns true when the
/// request should be allowed (token valid and human-scored, or service unavailable and
/// fail-open is enabled), false when it should be rejected. Never throws.
/// </summary>
public interface IRecaptchaVerifier
{
    /// <summary>
    /// Verifies <paramref name="token"/> for the expected <paramref name="action"/>.
    /// </summary>
    /// <param name="token">The reCAPTCHA token from the client.</param>
    /// <param name="action">The expected reCAPTCHA action (e.g. "change_password").</param>
    /// <param name="clientIp">The client IP, forwarded to the provider and used in logs.</param>
    /// <returns>True to allow the request, false to reject it.</returns>
    Task<bool> VerifyAsync(string token, string action, string clientIp);
}
```

- [ ] **Step 2: Create the test fake**

Create `src/PassReset.Tests.Windows/Fakes/FakeRecaptchaVerifier.cs`:

```csharp
using PassReset.Web.Services;

namespace PassReset.Tests.Windows.Fakes;

/// <summary>
/// In-memory <see cref="IRecaptchaVerifier"/> for controller tests. Returns a fixed result
/// and records every call so tests can assert on the forwarded action/IP.
/// </summary>
public sealed class FakeRecaptchaVerifier : IRecaptchaVerifier
{
    private readonly bool _result;
    public List<(string Token, string Action, string ClientIp)> Calls { get; } = new();

    public FakeRecaptchaVerifier(bool result) => _result = result;

    public Task<bool> VerifyAsync(string token, string action, string clientIp)
    {
        Calls.Add((token, action, clientIp));
        return Task.FromResult(_result);
    }
}
```

- [ ] **Step 3: Build the two projects to confirm the contract compiles**

Run: `dotnet build src/PassReset.Web/PassReset.Web.csproj -c Release`
Run: `dotnet build src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj -c Release`
Expected: both succeed (the interface + fake compile; nothing consumes them yet). Note: Tests.Windows already references PassReset.Web, so `PassReset.Web.Services` is visible.

- [ ] **Step 4: Commit**

```bash
git add src/PassReset.Web/Services/IRecaptchaVerifier.cs src/PassReset.Tests.Windows/Fakes/FakeRecaptchaVerifier.cs
git commit -m "feat(web): add IRecaptchaVerifier seam + test fake"
```

---

### Task 2: Implement `GoogleRecaptchaVerifier` (TDD, 7 branches)

Move the verification logic out of the controller into the production adapter, test-first against `FakeHttpMessageHandler`. The logic is lifted from `PasswordController.ValidateRecaptchaAsync` (lines 336–392) — behavior identical, action now a parameter.

**Files:**
- Create: `src/PassReset.Web/Services/GoogleRecaptchaVerifier.cs`
- Test: `src/PassReset.Tests.Windows/Web/Services/GoogleRecaptchaVerifierTests.cs`

**Interfaces:**
- Consumes: `IRecaptchaVerifier` (Task 1); `FakeHttpMessageHandler` (existing, `PassReset.Tests.Windows.Fakes`); `ClientSettings`/`Recaptcha` (`PassReset.Web.Models`).
- Produces: `GoogleRecaptchaVerifier(HttpClient http, IOptions<ClientSettings> clientSettings, ILogger<GoogleRecaptchaVerifier> logger) : IRecaptchaVerifier`.

- [ ] **Step 1: Write the failing tests (all 7 branches)**

Create `src/PassReset.Tests.Windows/Web/Services/GoogleRecaptchaVerifierTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PassReset.Web.Models;
using PassReset.Web.Services;
using PassReset.Tests.Windows.Fakes;
using Xunit;

namespace PassReset.Tests.Windows.Web.Services;

public class GoogleRecaptchaVerifierTests
{
    private static GoogleRecaptchaVerifier Build(
        FakeHttpMessageHandler handler, float threshold = 0.5f, bool failOpen = false)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.google.com/") };
        var settings = Options.Create(new ClientSettings
        {
            Recaptcha = new Recaptcha
            {
                Enabled = true,
                PrivateKey = "test-private-key",
                ScoreThreshold = threshold,
                FailOpenOnUnavailable = failOpen,
            },
        });
        return new GoogleRecaptchaVerifier(http, settings, NullLogger<GoogleRecaptchaVerifier>.Instance);
    }

    private static FakeHttpMessageHandler Json(string body) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task ValidHumanToken_MatchingAction_ReturnsTrue()
    {
        var v = Build(Json("""{"success":true,"score":0.9,"action":"change_password"}"""));
        Assert.True(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task ScoreBelowThreshold_ReturnsFalse()
    {
        var v = Build(Json("""{"success":true,"score":0.3,"action":"change_password"}"""), threshold: 0.5f);
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task WrongAction_ReturnsFalse()
    {
        var v = Build(Json("""{"success":true,"score":0.9,"action":"login"}"""));
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task SuccessFalse_ReturnsFalse()
    {
        var v = Build(Json("""{"success":false,"score":0.9,"action":"change_password"}"""));
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task Non2xx_FailOpenTrue_ReturnsTrue()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "");
        var v = Build(handler, failOpen: true);
        Assert.True(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task Non2xx_FailOpenFalse_ReturnsFalse()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "");
        var v = Build(handler, failOpen: false);
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task NetworkThrow_FailOpenTrue_ReturnsTrue()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("down"));
        var v = Build(handler, failOpen: true);
        Assert.True(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }

    [Fact]
    public async Task ParseError_NeverFailOpen_ReturnsFalse()
    {
        // 200 OK but unparseable body → unexpected error path, must NOT fail open even when failOpen=true.
        var v = Build(Json("not-json"), failOpen: true);
        Assert.False(await v.VerifyAsync("tok", "change_password", "1.2.3.4"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj --filter "FullyQualifiedName~GoogleRecaptchaVerifierTests"`
Expected: FAIL — compile error, `GoogleRecaptchaVerifier` does not exist.

- [ ] **Step 3: Implement the adapter**

Create `src/PassReset.Web/Services/GoogleRecaptchaVerifier.cs` (logic lifted verbatim from `PasswordController.ValidateRecaptchaAsync`, with `action` as a parameter):

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PassReset.Web.Models;

namespace PassReset.Web.Services;

/// <summary>
/// Verifies reCAPTCHA v3 tokens against Google's siteverify endpoint. Registered as a typed
/// HttpClient with BaseAddress https://www.google.com/. Honors
/// <see cref="Recaptcha.FailOpenOnUnavailable"/> for service outages but never fails open on
/// an unexpected/parse error.
/// </summary>
public sealed class GoogleRecaptchaVerifier : IRecaptchaVerifier
{
    private readonly HttpClient _http;
    private readonly IOptions<ClientSettings> _clientSettings;
    private readonly ILogger<GoogleRecaptchaVerifier> _logger;

    public GoogleRecaptchaVerifier(
        HttpClient http,
        IOptions<ClientSettings> clientSettings,
        ILogger<GoogleRecaptchaVerifier> logger)
    {
        _http = http;
        _clientSettings = clientSettings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string token, string action, string clientIp)
    {
        var config = _clientSettings.Value.Recaptcha;

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"]   = config.PrivateKey!,
                ["response"] = token,
                ["remoteip"] = clientIp,
            });

            var response = await _http.PostAsync("recaptcha/api/siteverify", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("reCAPTCHA API returned {StatusCode} for IP {ClientIp}",
                    response.StatusCode, clientIp);
                if (config.FailOpenOnUnavailable)
                {
                    _logger.LogWarning("reCAPTCHA fail-open enabled — allowing request through for IP {ClientIp}", clientIp);
                    return true;
                }
                return false;
            }

            var json = await response.Content.ReadFromJsonAsync<RecaptchaResponse>();
            return json?.Success == true
                && json.Score >= config.ScoreThreshold
                && string.Equals(json.Action, action, StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "reCAPTCHA service unreachable for IP {ClientIp}", clientIp);
            if (config.FailOpenOnUnavailable)
            {
                _logger.LogWarning("reCAPTCHA fail-open enabled — allowing request through for IP {ClientIp}", clientIp);
                return true;
            }
            return false;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "reCAPTCHA request timed out for IP {ClientIp}", clientIp);
            if (config.FailOpenOnUnavailable)
            {
                _logger.LogWarning("reCAPTCHA fail-open enabled — allowing request through for IP {ClientIp}", clientIp);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected errors (JSON parse, etc.) — never fail-open
            _logger.LogWarning(ex, "reCAPTCHA unexpected error for IP {ClientIp}", clientIp);
            return false;
        }
    }

    // Minimal DTO for reCAPTCHA v3 API response deserialization
    private sealed class RecaptchaResponse
    {
        public bool  Success { get; set; }
        public float Score   { get; set; }
        public string? Action { get; set; }
    }
}
```

> Note on the parse-error test: `ReadFromJsonAsync` on `"not-json"` throws (e.g. `JsonException`), caught by the final `catch (Exception)` → returns false even with fail-open. This matches today's behavior.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj --filter "FullyQualifiedName~GoogleRecaptchaVerifierTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PassReset.Web/Services/GoogleRecaptchaVerifier.cs src/PassReset.Tests.Windows/Web/Services/GoogleRecaptchaVerifierTests.cs
git commit -m "feat(web): implement GoogleRecaptchaVerifier with unit coverage"
```

---

### Task 3: Wire the seam into the controller and DI

Replace the controller's inline verification + HttpClient with the injected seam, and register the typed client. After this task the production code uses the seam end-to-end.

**Files:**
- Modify: `src/PassReset.Web/Controllers/PasswordController.cs`
- Modify: `src/PassReset.Web/Program.cs:138-142`

**Interfaces:**
- Consumes: `IRecaptchaVerifier` (Task 1), `GoogleRecaptchaVerifier` (Task 2).
- Produces: a controller with `IRecaptchaVerifier _recaptchaVerifier`; DI registration `AddHttpClient<IRecaptchaVerifier, GoogleRecaptchaVerifier>`.

- [ ] **Step 1: Inject the seam, drop the HttpClient plumbing**

In `src/PassReset.Web/Controllers/PasswordController.cs`:

Remove the field `private readonly HttpClient _recaptchaHttp;` (line 34) and add `private readonly IRecaptchaVerifier _recaptchaVerifier;`.

In the constructor: remove the `IHttpClientFactory httpClientFactory` parameter (line 52) and the `_recaptchaHttp = httpClientFactory.CreateClient("recaptcha");` assignment (line 67). Add the parameter `IRecaptchaVerifier recaptchaVerifier,` and the assignment `_recaptchaVerifier = recaptchaVerifier;`.

- [ ] **Step 2: Update both call sites to use the seam**

In `PostAsync` (the block at lines 166–173), replace the inner call:

```csharp
        var recaptchaConfig = settings.Recaptcha;
        if (recaptchaConfig?.Enabled == true && !string.IsNullOrWhiteSpace(recaptchaConfig.PrivateKey))
        {
            if (!await _recaptchaVerifier.VerifyAsync(model.Recaptcha, "change_password", clientIp))
            {
                Audit("RecaptchaFailed", model.Username, clientIp, SiemEventType.RecaptchaFailed);
                return BadRequest(ApiResult.InvalidCaptcha());
            }
        }
```

In `StatusAsync` (the block at lines 250–257), replace the inner call identically (action stays `"change_password"` to preserve current behavior):

```csharp
        var recaptchaConfig = settings.Recaptcha;
        if (recaptchaConfig?.Enabled == true && !string.IsNullOrWhiteSpace(recaptchaConfig.PrivateKey))
        {
            if (!await _recaptchaVerifier.VerifyAsync(model.Recaptcha, "change_password", clientIp))
            {
                Audit("StatusRecaptchaFailed", model.Username, clientIp, SiemEventType.RecaptchaFailed);
                return BadRequest(ApiResult.InvalidCaptcha());
            }
        }
```

- [ ] **Step 3: Delete the now-dead controller code**

Delete the entire `private async Task<bool> ValidateRecaptchaAsync(...)` method (lines 336–393) and the private nested `RecaptchaResponse` class (lines 395–401). Remove the now-unused `using System.Net.Http.Json;` (line 1) **only if** no other code in the file uses it — verify with a search for `ReadFromJsonAsync`/`PostAsJsonAsync` in the file first; if none remain, remove the using.

- [ ] **Step 4: Register the typed client in DI**

In `src/PassReset.Web/Program.cs`, replace the named client (lines 138–142):

```csharp
    builder.Services.AddHttpClient<IRecaptchaVerifier, GoogleRecaptchaVerifier>(c =>
    {
        c.BaseAddress = new Uri("https://www.google.com/");
        c.Timeout = TimeSpan.FromSeconds(10);
    });
```

Add `using PassReset.Web.Services;` to Program.cs if not already present (verify first — it likely is, since `ISiemService`/`IEmailService` are registered there).

- [ ] **Step 5: Build the Web project**

Run: `dotnet build src/PassReset.Web/PassReset.Web.csproj -c Release`
Expected: SUCCESS, 0 errors (the only warning is the pre-existing ASP0000 at Program.cs:203). If the build complains about an unused `IHttpClientFactory` using or `System.Net.Http.Json`, remove the now-dead using.

- [ ] **Step 6: Commit**

```bash
git add src/PassReset.Web/Controllers/PasswordController.cs src/PassReset.Web/Program.cs
git commit -m "refactor(web): route reCAPTCHA through IRecaptchaVerifier seam, register typed client"
```

---

### Task 4: Migrate controller reCAPTCHA tests to the fake; prove the suite green

Replace the `HttpMessageHandler`-based `StubbedRecaptchaFactory` with `FakeRecaptchaVerifier` registration, fix the named-client test, and run the full suite.

**Files:**
- Modify: `src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs`

**Interfaces:**
- Consumes: `FakeRecaptchaVerifier` (Task 1).

- [ ] **Step 1: Survey the reCAPTCHA tests and how they use `StubbedRecaptchaFactory`**

```bash
cd "c:/Users/Phibu/Claude-Projekte/AD-Passreset-Portal"
grep -n "StubbedRecaptchaFactory\|StubRecaptchaHandler\|RecaptchaEnabledFactory\|Recaptcha_NamedHttpClient_IsRegistered\|_responder\|JsonOk\|new StubbedRecaptchaFactory" src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs
```
Expected: a handful of test methods construct `new StubbedRecaptchaFactory(responder, failOpen)` with a `responder` lambda returning a `JsonOk(...)` / 500 / throw. Each maps to one of: low-score-reject, fail-open-allow, fail-closed-reject. Note each test's intended outcome (allow vs reject) — that outcome becomes the `FakeRecaptchaVerifier(result)` boolean.

- [ ] **Step 2: Replace `StubbedRecaptchaFactory`'s service swap with a fake-verifier registration**

In `StubbedRecaptchaFactory.ConfigureWebHost`, replace the `ConfigureTestServices` block that re-registers the `"recaptcha"` HttpClient + `StubRecaptchaHandler` with a fake-verifier registration. Change the factory's constructor to take the desired boolean outcome instead of an HTTP responder:

```csharp
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
```

Add the usings at the top of the file if missing: `using Microsoft.Extensions.DependencyInjection.Extensions;` (for `RemoveAll`), `using PassReset.Web.Services;`, `using PassReset.Tests.Windows.Fakes;`. Delete the now-unused `StubRecaptchaHandler` nested class and the `JsonOk` helper if nothing else references them (the Step 1 grep tells you; the real-Google `RecaptchaEnabledFactory` does not use them).

- [ ] **Step 3: Update each test that constructed the factory**

For each test found in Step 1 that did `new StubbedRecaptchaFactory(responder, failOpen)`, replace with `new StubbedRecaptchaFactory(verifyResult)` where `verifyResult` is the outcome that test asserts:
- a test that previously stubbed a low score and asserted the request was rejected → `new StubbedRecaptchaFactory(false)`
- a test that previously stubbed a 500 with `failOpen: true` and asserted the request went through → `new StubbedRecaptchaFactory(true)`
- a test that previously stubbed a 500 with `failOpen: false` and asserted rejection → `new StubbedRecaptchaFactory(false)`

The assertions (HTTP status, response body) do NOT change — only how the reCAPTCHA outcome is produced. The `FailOpenOnUnavailable` config key is dropped from the factory because fail-open is now the verifier's concern and is unit-tested in Task 2; at the controller level only the boolean verdict matters.

> If a test's intent is specifically "fail-open behavior at the controller level" and removing it would lose coverage, keep its assertion but express the outcome as `FakeRecaptchaVerifier(true)` — the controller cannot distinguish "passed" from "failed-open-allowed", which is correct: that distinction is the verifier's responsibility, already covered by `Non2xx_FailOpenTrue_ReturnsTrue` in Task 2.

- [ ] **Step 4: Fix the named-client registration test**

The test `Recaptcha_NamedHttpClient_IsRegistered` (line ~59) asserts `clientFactory.CreateClient("recaptcha")` works. The named client no longer exists (it's now a typed client). Replace this test with one asserting the seam is registered:

```csharp
    [Fact]
    public void RecaptchaVerifier_IsRegistered()
    {
        using var factory = new BasicFactory();   // or whichever minimal factory the file already uses
        using var scope = factory.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetService<IRecaptchaVerifier>();
        Assert.NotNull(verifier);
        Assert.IsType<GoogleRecaptchaVerifier>(verifier);
    }
```

> Use the file's existing minimal `WebApplicationFactory` subclass (the one used by the rate-limit tests with `UseDebugProvider=true`). If the only factories are `StubbedRecaptchaFactory` and `RecaptchaEnabledFactory`, resolve via `RecaptchaEnabledFactory` instead (it registers real keys but you are only resolving the type, not calling Google) — or add a tiny bare factory. Pick whichever keeps the test self-contained; the assertion is type registration, not behavior. Add `using PassReset.Web.Services;` if needed.

- [ ] **Step 5: Run the reCAPTCHA test file**

Run: `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj --filter "FullyQualifiedName~RateLimitAndRecaptchaTests"`
Expected: PASS — all rate-limit and reCAPTCHA tests green (excluding the real-Google `RecaptchaEnabledFactory` test if it requires network; it is left untouched and may be `[Fact(Skip=...)]` or network-gated as it was before).

- [ ] **Step 6: Run the full solution suite**

Run: `dotnet build src/PassReset.sln -c Release` then `dotnet test src/PassReset.sln -c Release`
Expected: all green. Count = prior 375 passing + 8 new `GoogleRecaptchaVerifierTests` − any net change from the named-client test swap (1 replaced, not removed). Report exact totals per project.

- [ ] **Step 7: Commit**

```bash
git add src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs
git commit -m "test(web): drive controller reCAPTCHA tests via FakeRecaptchaVerifier"
```

---

## Self-Review

**Spec coverage** (against the grilling decisions):
- Q1 verify-only seam (controller keeps gate/audit/response) → Task 3 Steps 1–2 keep the gate + `Audit` + `InvalidCaptcha`; only `VerifyAsync` is behind the seam. ✓
- Q2 action as parameter, both pass `"change_password"` → Task 1 signature; Task 3 Step 2 both call sites pass `"change_password"`. ✓
- Q3 typed client; controller drops `IHttpClientFactory`+`_recaptchaHttp` → Task 3 Steps 1, 4. ✓
- Q4 real seam (Google + Fake) → Task 1 fake, Task 2 Google. ✓
- Q5 migrate controller tests to fake; unit-test Google via FakeHttpMessageHandler; real-Google test untouched → Task 2 (unit), Task 4 (migration), Task 4 Step 3 note leaves `RecaptchaEnabledFactory` alone. ✓
- Q6 `PassReset.Web/Services`, RecaptchaResponse private in adapter → Task 1/2 locations; Task 2 Step 3 DTO is private nested; Task 3 Step 3 deletes the controller's copy. ✓
- Q7 TDD 7 branches + full suite regression → Task 2 (8 tests: the 7 branches + matching-action happy path), Task 4 Step 6. ✓

**Placeholder scan:** No TBD/TODO; every code step has full code; commands have expected output. The only judgment-dependent step is Task 4 Step 3 (mapping each existing test's outcome to a boolean) — unavoidable since the exact test list is read at execution time; Step 1 surfaces it and the mapping rule is explicit. ✓

**Type consistency:** `IRecaptchaVerifier.VerifyAsync(string token, string action, string clientIp) → Task<bool>` identical across Tasks 1→2→3→4. `FakeRecaptchaVerifier(bool result)` / `GoogleRecaptchaVerifier(HttpClient, IOptions<ClientSettings>, ILogger<...>)` consistent. ✓

**Note for the implementer (Task 4 Step 4):** confirm whether `Microsoft.Extensions.DependencyInjection.Extensions` (`RemoveAll`) is already imported in the test file; the existing `ConfigureTestServices` re-registration suggests the swap pattern is established.
