# Implementation Plan: Security & Backend Issue Closure (#28, #29, #30, #32, #33, #36, #38)

**Goal:** Drive seven still-partial GitHub issues to full closure by adding the missing tests, production-code seams, installer validation, and docs identified in the pinned gap analysis (`.tmp_tasks/plan_inputs/security.json`). Every acceptance criterion for each issue must be provably met, with regression guards for medium/high-risk changes.

**Architecture:** ASP.NET Core 10 (Windows-only host) with a provider-pattern password backend (`IPasswordChangeProvider` → `LockoutPasswordChangeProvider` decorator → Windows `PasswordChangeProvider` / cross-platform `LdapPasswordChangeProvider`). Security events flow through `ISiemService` (legacy `LogEvent(SiemEventType,…)` + structured `LogEvent(AuditEvent)`). The React 19 / MUI 6 frontend fetches policy from `GET /api/password/policy`. Deployment is via `deploy/Install-PassReset.ps1` (IIS/Service/Console modes).

**Tech Stack:** C# 13 / xUnit v3 (`src/PassReset.Tests` cross-platform, `src/PassReset.Tests.Windows` Windows-only integration via `WebApplicationFactory<Program>`); TypeScript / React / Vitest + RTL (`src/PassReset.Web/ClientApp`); PowerShell / Pester (`deploy/Install-PassReset.Tests.ps1`); GitHub Actions.

> **Agentic-worker sub-skill note:** Execute this plan with `superpowers:subagent-driven-development` (one task per subagent, implementer → spec-review → code-quality loop). Each task is independently committable. Use `superpowers:test-driven-development` discipline per task: failing test first, then minimal implementation.

**Plan execution order:** This is plan 2 of 4. It runs **after the schema plan (#27)**. Within this plan, **#28 precedes #30** (audit wiring builds on the error-mapping invariants). **#36 and #38 depend on #27 schema work being merged first.**

---

## File Structure

**Modified (production code):**
- `src/PassReset.Web/Controllers/PasswordController.cs` — (#28) document lockout-exclusion rationale on `IsAccountEnumerationCode`; (#29) replace static `_recaptchaHttp` with DI-injected named HttpClient; (#30) refactor `Audit()` to emit structured `AuditEvent` with `traceId` and add `PasswordChangeAttemptStarted`.
- `src/PassReset.Web/Program.cs` — (#29) register named `"recaptcha"` HttpClient.
- `src/PassReset.Web/Services/ISiemService.cs` — (#30) add `PasswordChangeAttemptStarted` to `SiemEventType`.
- `src/PassReset.Web/ClientApp/src/components/AdPasswordPolicyPanel.tsx` — (#38) render `minAgeDays` / `maxAgeDays`.
- `src/PassReset.PasswordProvider/PasswordChangeProvider.cs` — (#36) extract `ClassifyChangePasswordHResult` pure helper + add `UnauthorizedAccessException` catch.
- `src/PassReset.PasswordProvider.Ldap/LdapPasswordChangeProvider.cs` — (#38) second Base-scope domain-root search for `pwdProperties`/`pwdHistoryLength`.
- `src/PassReset.PasswordProvider.Ldap/LdapAttributeNames.cs` — (#38) add `PwdProperties` / `PwdHistoryLength` / `DefaultNamingContext` constants.
- `deploy/Install-PassReset.ps1` — (#32) add `Test-HttpsBinding` function + final-binding-config output block.

**Modified (tests):**
- `src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs` — (#28) lockout-enabled factory + 2 lockout tests.
- `src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs` — (#29) 4 new reCAPTCHA tests + stub handler.
- `src/PassReset.Tests.Windows/Web/Controllers/PasswordControllerTests.cs` — (#30) structured-audit wiring tests.
- `src/PassReset.Tests.Windows/Web/Startup/EnvironmentVariableOverrideTests.cs` — (#33) 2 LDAP secret override tests.
- `src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs` — (#33) LDAP-secret no-leak sentinels.
- `src/PassReset.Tests.Windows/PasswordProvider/ChangePasswordHResultTests.cs` *(new)* — (#36) pure-helper classification tests.
- `src/PassReset.Tests/Services/LdapPasswordChangeProviderTests.cs` — (#38) domain-root policy tests.
- `src/PassReset.Web/ClientApp/src/components/__tests__/AdPasswordPolicyPanel.test.tsx` — (#38) age-rendering tests.
- `deploy/Install-PassReset.Tests.ps1` — (#32) `Test-HttpsBinding` Pester tests.

**Created (docs):**
- `docs/Audit-Events.md` — (#30) structured audit event reference.
- `docs/Password-Policy.md` — (#38) policy-display behavior, LDAP limitations, FGPP scope.
- `docs/Secret-Management.md` — (#33) corrected line 65 (modified, not created).

---

## Issue #28 (STAB-013) — Account-enumeration oracle: lockout codes

**Sequencing:** Must complete before #30. `risk: low`, `dependsOn: []`.

Confirmed against HEAD: `RedactIfProduction` (PasswordController.cs:264-267) only collapses `InvalidCredentials`/`UserNotFound`; `ApproachingLockout`/`PortalLockout` already pass through. The lockout decorator (`LockoutPasswordChangeProvider.cs:100,122`) returns `ApproachingLockout` at `newCount == threshold` and `PortalLockout` at `threshold+1`. The debug provider returns `InvalidCredentials` for username `invalidCredentials`. The rate limiter permits 5 req/5 min/IP, so a 4-POST sequence fits. The gap is purely **missing test coverage + missing code doc**.

### Task 28.1 — Add lockout-enabled production factory + ApproachingLockout wire/SIEM test

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs` (add factory after line 176; add test after line 316)

- [ ] **Step 1 — Write failing test.** First add the lockout-enabled recording factory immediately after the closing brace of `ProductionEnvFactoryWithRecorder` (after line 176):

```csharp
    /// <summary>
    /// STAB-013 gap closure: Production env + recording SIEM + portal lockout ENABLED
    /// (PortalLockoutThreshold=3, not 0). Lets tests drive the lockout decorator's
    /// ApproachingLockout (failure #3) and PortalLockout (failure #4) codes and assert
    /// they are NOT collapsed to Generic on the wire — they leak only per-account
    /// throttling state, never directory membership.
    /// </summary>
    public sealed class ProductionEnvFactoryWithEnabledLockout : WebApplicationFactory<Program>
    {
        public RecordingSiemService Recorder { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var cfg = TestConfig();
                cfg["PasswordChangeOptions:PortalLockoutThreshold"] = "3"; // enable lockout
                config.AddInMemoryCollection(cfg);
            });
            builder.ConfigureTestServices(services =>
            {
                SwapInDebugProvider(services);
                SwapInRecordingSiem(services, Recorder);
            });
        }
    }
```

Then add the test after line 316 (before the final closing brace):

```csharp
    /// <summary>
    /// STAB-013 gap closure: with portal lockout enabled, the THIRD invalid-credential
    /// attempt is upgraded by the decorator to ApproachingLockout. This code is NOT an
    /// account-enumeration oracle (it reveals only that THIS portal is throttling THIS
    /// account, not whether the account exists in AD), so it must reach the wire intact
    /// in Production — and the SIEM must record the granular ApproachingLockout event.
    /// </summary>
    [Fact]
    public async Task Production_ApproachingLockout_WirePreservesCode()
    {
        using var factory = new ProductionEnvFactoryWithEnabledLockout();
        using var client  = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = null!;
        // Failures 1-2 pass through as InvalidCredentials; failure 3 hits threshold → ApproachingLockout.
        for (int i = 0; i < 3; i++)
            response = await client.PostAsJsonAsync("/api/password", MakeRequest("invalidCredentials"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResultAsync(response);
        Assert.NotNull(result);
        Assert.Single(result!.Errors);
        Assert.Equal(ApiErrorCode.ApproachingLockout, result.Errors[0].ErrorCode);
        Assert.Contains(SiemEventType.ApproachingLockout, factory.Recorder.Events);
    }
```

- [ ] **Step 2 — Run, expect FAIL** (test not yet wired / asserts new factory):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests.Production_ApproachingLockout_WirePreservesCode"`
- [ ] **Step 3 — Minimal implementation.** No production change required — the test passes once the factory + test compile, because the controller already preserves the code. (This task is a coverage gap; the "implementation" is the new factory + test code above.)
- [ ] **Step 4 — Run, expect PASS** (same command as Step 2).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs && git commit -m "test(web): prove ApproachingLockout reaches wire in Production [STAB-013]"`

### Task 28.2 — Add PortalLockout wire/SIEM test

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs` (add test after the Task 28.1 test)

- [ ] **Step 1 — Write failing test:**

```csharp
    /// <summary>
    /// STAB-013 gap closure: the FOURTH invalid-credential attempt is blocked by the
    /// decorator BEFORE contacting AD and returns PortalLockout. Like ApproachingLockout,
    /// this is per-account throttling state, not a directory-enumeration vector, so it
    /// must NOT be collapsed to Generic in Production. SIEM records PortalLockout.
    /// </summary>
    [Fact]
    public async Task Production_PortalLockout_WirePreservesCode()
    {
        using var factory = new ProductionEnvFactoryWithEnabledLockout();
        using var client  = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = null!;
        // 4 failures: #1-2 InvalidCredentials, #3 ApproachingLockout, #4 PortalLockout (AD not contacted).
        for (int i = 0; i < 4; i++)
            response = await client.PostAsJsonAsync("/api/password", MakeRequest("invalidCredentials"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadResultAsync(response);
        Assert.NotNull(result);
        Assert.Single(result!.Errors);
        Assert.Equal(ApiErrorCode.PortalLockout, result.Errors[0].ErrorCode);
        Assert.Contains(SiemEventType.PortalLockout, factory.Recorder.Events);
    }
```

- [ ] **Step 2 — Run, expect FAIL** (before this test exists it's absent; run to confirm compile/discovery):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests.Production_PortalLockout_WirePreservesCode"`
- [ ] **Step 3 — Minimal implementation.** None — controller already preserves the code.
- [ ] **Step 4 — Run, expect PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs && git commit -m "test(web): prove PortalLockout reaches wire in Production [STAB-013]"`

### Task 28.3 — Document the lockout-exclusion rationale in code

**Files:** `src/PassReset.Web/Controllers/PasswordController.cs` (lines 255-257)

- [ ] **Step 1 — Regression guard already in place.** The two tests from 28.1/28.2 lock the behavior. No new test needed; this is a doc-only change asserted by those tests.
- [ ] **Step 2 — Confirm tests green before editing** (regression baseline):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests"`
- [ ] **Step 3 — Edit the comment.** Replace the single-line summary on `IsAccountEnumerationCode` (line 255):

```csharp
    /// <summary>
    /// STAB-013 D-01: account-enumeration codes that MUST collapse to Generic on the wire
    /// in Production. <see cref="ApiErrorCode.InvalidCredentials"/> and
    /// <see cref="ApiErrorCode.UserNotFound"/> leak whether a username exists in AD, so they
    /// are redacted. Deliberately EXCLUDED: <see cref="ApiErrorCode.ApproachingLockout"/> and
    /// <see cref="ApiErrorCode.PortalLockout"/> — these leak only per-account portal-throttling
    /// state (this portal is rate-limiting this account), never directory membership, so they
    /// are safe to expose and are NOT an enumeration vector. SIEM granularity is preserved
    /// independently (D-05); see GenericErrorMappingTests.Production_ApproachingLockout_*
    /// / Production_PortalLockout_* for the regression guard.
    /// </summary>
```

- [ ] **Step 4 — Run, expect PASS** (proves the doc change didn't alter behavior):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests"`
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/Controllers/PasswordController.cs && git commit -m "docs(web): justify lockout-code redaction exclusion [STAB-013]"`

### Task 28.4 — Verify acceptance criteria & close #28

- [ ] Run the full mapping suite: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests"` — all (4 original + 4 SIEM + 2 lockout) green.
- [ ] Confirm each criterion: (a) lockout codes proven non-collapsed in Production with SIEM granularity; (b) production error-shape coverage now includes ApproachingLockout/PortalLockout; (c) code comment documents the exclusion rationale.
- [ ] Close: `gh issue close 28 --reason completed --comment "Closed by STAB-013 gap closure: added ProductionEnvFactoryWithEnabledLockout + Production_ApproachingLockout_WirePreservesCode + Production_PortalLockout_WirePreservesCode proving lockout codes reach the wire intact (per-account throttling, not an enumeration oracle) while SIEM stays granular; documented the redaction-exclusion rationale on IsAccountEnumerationCode."`

---

## Issue #29 (STAB-014) — reCAPTCHA failure modes are testable & enforced

**`risk: medium`, `dependsOn: []`.** Regression risk: the reCAPTCHA HttpClient is currently a `static readonly` field shared process-wide. Moving it to DI changes a hot-path socket strategy. **What could break:** socket exhaustion if the named client isn't pooled; the existing `Recaptcha_EnabledWithInvalidToken_ReturnsInvalidCaptcha` real-Google test must still pass. We register a named `IHttpClientFactory` client (same pattern as `"pwned"`) which preserves pooling, and keep the existing real-Google test as the regression guard.

### Task 29.1 — Register named "recaptcha" HttpClient in Program.cs (regression-safe seam)

**Files:** `src/PassReset.Web/Program.cs` (after the `"pwned"` registration, lines 124-128)

- [ ] **Step 1 — Write failing test.** Add to `RateLimitAndRecaptchaTests.cs` a guard proving the named client is resolvable (forces the registration to exist). Add near the top of the class body (after `MakeRequest`, ~line 53):

```csharp
    [Fact]
    public void Recaptcha_NamedHttpClient_IsRegistered()
    {
        using var factory = new RateLimitFactory();
        var clientFactory = factory.Services
            .GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = clientFactory.CreateClient("recaptcha");
        Assert.Equal(new Uri("https://www.google.com/"), client.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
    }
```

Add `using Microsoft.Extensions.DependencyInjection;` to the test file's usings if absent.

- [ ] **Step 2 — Run, expect FAIL** (named client not yet registered → `CreateClient("recaptcha")` returns default BaseAddress null):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests.Recaptcha_NamedHttpClient_IsRegistered"`
- [ ] **Step 3 — Minimal implementation.** Insert after line 128 in `Program.cs` (right after the `"pwned"` `AddHttpClient` block closes):

```csharp
    // STAB-014: reCAPTCHA v3 verification client. Named client (not a static field) so
    // tests can inject a stub handler to exercise low-score / unreachable fail-safe paths
    // without hitting Google. PooledConnectionLifetime via the factory respects DNS changes.
    builder.Services.AddHttpClient("recaptcha", c =>
    {
        c.BaseAddress = new Uri("https://www.google.com/");
        c.Timeout = TimeSpan.FromSeconds(10);
    });
```

- [ ] **Step 4 — Run, expect PASS** (same command as Step 2).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/Program.cs src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs && git commit -m "feat(web): register named recaptcha HttpClient for testable verification [STAB-014]"`

### Task 29.2 — Consume the injected HttpClient in PasswordController

**Files:** `src/PassReset.Web/Controllers/PasswordController.cs` (field 37-46; constructor 48-70; `ValidateRecaptchaAsync` line 281)

- [ ] **Step 1 — Regression guard test.** The existing real-Google `Recaptcha_EnabledWithInvalidToken_ReturnsInvalidCaptcha` is the guard. Run it before changing code:
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests.Recaptcha_EnabledWithInvalidToken_ReturnsInvalidCaptcha"` (expect PASS — baseline).
- [ ] **Step 2 — (covered by Step 1 baseline + 29.3 mock tests).**
- [ ] **Step 3 — Minimal implementation.** Delete the static field (lines 37-46) and replace with an instance field; add the parameter to the constructor; use it in `ValidateRecaptchaAsync`.

  Remove lines 37-46 (`private static readonly HttpClient _recaptchaHttp = …;`). Add instance field after line 31 (`private readonly ILogger<PasswordController> _logger;`):

```csharp
    private readonly HttpClient _recaptchaHttp;
```

  Add a constructor parameter (after `ILogger<PasswordController> logger`, line 58) and assignment. Change the signature to inject `IHttpClientFactory`:

```csharp
        IHttpClientFactory httpClientFactory,
        ILogger<PasswordController> logger)
    {
        ...
        _logger             = logger;
        _recaptchaHttp      = httpClientFactory.CreateClient("recaptcha");
    }
```

  No change needed inside `ValidateRecaptchaAsync` — it already calls `_recaptchaHttp.PostAsync(...)`. Add `using System.Net.Http;` only if not already implied (it is via implicit usings; verify build).

- [ ] **Step 4 — Run, expect PASS** (real-Google regression guard + named-client test still green):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests"`
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/Controllers/PasswordController.cs && git commit -m "refactor(web): inject recaptcha HttpClient via factory, drop static field [STAB-014]"`

### Task 29.3 — Add stub-handler factory + empty-token rejection test

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs` (add stub handler + factory + test)

- [ ] **Step 1 — Write failing test.** Add a stub `HttpMessageHandler` and a factory that overrides the named client, then the empty-token test. Add the stub class and factory inside the test class:

```csharp
    /// <summary>
    /// STAB-014: scriptable HttpMessageHandler so reCAPTCHA verification can be driven to
    /// any siteverify outcome (low score, HTTP 500, network throw) without hitting Google.
    /// </summary>
    private sealed class StubRecaptchaHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubRecaptchaHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    /// <summary>
    /// reCAPTCHA enabled, with the named "recaptcha" HttpClient's primary handler swapped
    /// for a scripted stub. FailOpenOnUnavailable defaults to the supplied value.
    /// </summary>
    private sealed class StubbedRecaptchaFactory : WebApplicationFactory<Program>
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly bool _failOpen;
        public StubbedRecaptchaFactory(
            Func<HttpRequestMessage, HttpResponseMessage> responder, bool failOpen)
        {
            _responder = responder;
            _failOpen  = failOpen;
        }
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
                    ["ClientSettings:Recaptcha:FailOpenOnUnavailable"] = _failOpen ? "true" : "false",
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
                services.AddHttpClient("recaptcha", c =>
                {
                    c.BaseAddress = new Uri("https://www.google.com/");
                    c.Timeout = TimeSpan.FromSeconds(10);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new StubRecaptchaHandler(_responder));
            });
        }
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Recaptcha_EnabledWithEmptyToken_Rejects()
    {
        // Empty token: ValidateRecaptchaAsync posts response="" → Google (stub) returns
        // success=false → rejected. We stub success=false to keep the test offline.
        using var factory = new StubbedRecaptchaFactory(
            _ => JsonOk("{\"success\":false}"), failOpen: false);
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
```

Add usings: `using Microsoft.Extensions.DependencyInjection;` and `using Microsoft.AspNetCore.TestHost;` if absent.

- [ ] **Step 2 — Run, expect FAIL → then PASS once handler wiring compiles.** Run:
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests.Recaptcha_EnabledWithEmptyToken_Rejects"` (FAILS until 29.2's injection seam is consumed — it depends on 29.2).
- [ ] **Step 3 — Minimal implementation.** None beyond 29.2; the stub relies on the injected client.
- [ ] **Step 4 — Run, expect PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs && git commit -m "test(web): reject empty reCAPTCHA token when enabled [STAB-014]"`

### Task 29.4 — Low-score rejection test

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs`

- [ ] **Step 1 — Write failing test:**

```csharp
    [Fact]
    public async Task Recaptcha_LowScore_ReturnsInvalidCaptcha()
    {
        // Stub Google returns success=true but score 0.3 < 0.5 threshold → reject.
        using var factory = new StubbedRecaptchaFactory(
            _ => JsonOk("{\"success\":true,\"score\":0.3,\"action\":\"change_password\"}"),
            failOpen: false);
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
```

- [ ] **Step 2 — Run, expect FAIL/discovery:**
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests.Recaptcha_LowScore_ReturnsInvalidCaptcha"`
- [ ] **Step 3 — Minimal implementation.** None.
- [ ] **Step 4 — Run, expect PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs && git commit -m "test(web): reject low-score reCAPTCHA below threshold [STAB-014]"`

### Task 29.5 — Provider-unreachable fail-CLOSE test (FailOpenOnUnavailable=false → 400)

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs`

- [ ] **Step 1 — Write failing test:**

```csharp
    [Fact]
    public async Task Recaptcha_ProviderUnreachable_FailSafeDisabled_Returns400()
    {
        // Stub throws HttpRequestException → ValidateRecaptchaAsync catch with
        // FailOpenOnUnavailable=false → returns false → InvalidCaptcha.
        using var factory = new StubbedRecaptchaFactory(
            _ => throw new HttpRequestException("simulated network failure"),
            failOpen: false);
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
```

- [ ] **Step 2 — Run, expect FAIL/discovery:**
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests.Recaptcha_ProviderUnreachable_FailSafeDisabled_Returns400"`
- [ ] **Step 3 — Minimal implementation.** None (existing `catch (HttpRequestException)` at PasswordController.cs:300 already returns false when fail-open is off).
- [ ] **Step 4 — Run, expect PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs && git commit -m "test(web): fail-closed reCAPTCHA rejects on provider outage [STAB-014]"`

### Task 29.6 — Provider-unreachable fail-OPEN test (FailOpenOnUnavailable=true → 200)

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs`

- [ ] **Step 1 — Write failing test:**

```csharp
    [Fact]
    public async Task Recaptcha_ProviderUnreachable_FailSafeEnabled_Returns200()
    {
        // Same network throw, but FailOpenOnUnavailable=true → request proceeds (200).
        // Username "alice" is unknown to the debug provider → success (null error).
        using var factory = new StubbedRecaptchaFactory(
            _ => throw new HttpRequestException("simulated network failure"),
            failOpen: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var req = MakeRequest("alice");
        req.Recaptcha = "any-token";

        var response = await client.PostAsJsonAsync("/api/password", req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

- [ ] **Step 2 — Run, expect FAIL/discovery:**
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests.Recaptcha_ProviderUnreachable_FailSafeEnabled_Returns200"`
- [ ] **Step 3 — Minimal implementation.** None (existing fail-open path at PasswordController.cs:303-307 returns true).
- [ ] **Step 4 — Run, expect PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs && git commit -m "test(web): fail-open reCAPTCHA allows request on provider outage [STAB-014]"`

### Task 29.7 — Verify acceptance criteria & close #29

- [ ] Run full suite: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests"` — all 9 green (2 rate-limit, 2 original reCAPTCHA, 1 named-client, 4 new).
- [ ] Confirm criteria: failure mode is fail-safe and now testable; tests cover allowed/rate-limited/captcha-missing/low-score/provider-unreachable (both fail-modes); enforcement of low-score & missing-token rejection proven.
- [ ] Close: `gh issue close 29 --reason completed --comment "Closed by STAB-014: injected the reCAPTCHA verification HttpClient via a named IHttpClientFactory client (dropped the static field), then added Recaptcha_EnabledWithEmptyToken_Rejects, Recaptcha_LowScore_ReturnsInvalidCaptcha, and provider-unreachable fail-close/fail-open tests using a scripted StubRecaptchaHandler. Existing real-Google invalid-token test retained as regression guard."`

---

## Issue #30 (STAB-015) — Structured audit events with correlation IDs

**Sequencing:** after #28. **`risk: medium`, `dependsOn: [13,19,28]`.** Regression risk: rewiring every `Audit()` call. **What could break:** if the new `AuditEvent` path skips the legacy SIEM emission, syslog/email alert delivery regresses; the `Production_*_SiemRemainsGranular` tests from #28 must still pass (they assert the controller emits the granular `SiemEventType`). We keep `LogEvent(AuditEvent)` calls (the `RecordingSiemService` records `evt.EventType`, so #28's tests still observe the granular type). Confirmed: `RateLimitExceeded` is **already** emitted via `Program.cs` `OnRejected` (line 407) using the legacy overload — the gap is structured wiring + `PasswordChangeAttemptStarted`, not "no event".

### Task 30.1 — Add PasswordChangeAttemptStarted to SiemEventType

**Files:** `src/PassReset.Web/Services/ISiemService.cs` (enum, after line 36); `src/PassReset.Web/Services/SiemService.cs` (SeverityMap, line 28)

- [ ] **Step 1 — Write failing test.** Add to `AuditEventRedactionTests.cs` a test asserting the new event type exists and has a severity mapping. Append a new test method:

```csharp
    [Fact]
    public void SiemEventType_HasPasswordChangeAttemptStarted()
    {
        Assert.True(Enum.IsDefined(typeof(SiemEventType), "PasswordChangeAttemptStarted"));
    }
```

- [ ] **Step 2 — Run, expect FAIL:**
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~AuditEventRedactionTests.SiemEventType_HasPasswordChangeAttemptStarted"`
- [ ] **Step 3 — Minimal implementation.** Add to the enum in `ISiemService.cs` after `Generic` (line 36) — note: append at the END to avoid renumbering existing values:

```csharp
    /// <summary>A password-change attempt has entered the controller (correlation anchor).</summary>
    PasswordChangeAttemptStarted,
```

  Add a severity entry to `SiemService.cs` `SeverityMap` (after line 28, before the closing `}`):

```csharp
        [SiemEventType.PasswordChangeAttemptStarted] = 6, // Informational
```

- [ ] **Step 4 — Run, expect PASS** (same command as Step 2).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/Services/ISiemService.cs src/PassReset.Web/Services/SiemService.cs src/PassReset.Tests.Windows/Web/Services/AuditEventRedactionTests.cs && git commit -m "feat(web): add PasswordChangeAttemptStarted SIEM event [STAB-015]"`

### Task 30.2 — Refactor Audit() to emit structured AuditEvent with traceId

**Files:** `src/PassReset.Web/Controllers/PasswordController.cs` (Audit helper, lines 234-243; callers at 154,164,176,198,204)

- [ ] **Step 1 — Write failing test.** Add to `GenericErrorMappingTests.cs` (which already has `RecordingSiemService` capturing `AuditEvent.EventType` and a Production factory) — but assert the structured path carries a non-null `TraceId`. Extend `RecordingSiemService` (lines 37-43) to also capture full events, then add a test. First extend the recorder:

```csharp
        public List<AuditEvent> AuditEvents { get; } = new();
        public void LogEvent(AuditEvent evt) { Events.Add(evt.EventType); AuditEvents.Add(evt); }
```

  (Replace the existing one-line `LogEvent(AuditEvent evt)` at line 42.) Then add a test:

```csharp
    /// <summary>
    /// STAB-015: every password-change branch must emit a structured AuditEvent carrying a
    /// non-empty TraceId so SOC tooling can correlate the failure across logs.
    /// </summary>
    [Fact]
    public async Task Production_InvalidCredentials_EmitsStructuredAuditWithTraceId()
    {
        using var factory = new ProductionEnvFactoryWithRecorder();
        using var client  = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        await client.PostAsJsonAsync("/api/password", MakeRequest("invalidCredentials"));

        var failEvent = factory.Recorder.AuditEvents
            .FirstOrDefault(e => e.EventType == SiemEventType.InvalidCredentials);
        Assert.NotNull(failEvent);
        Assert.False(string.IsNullOrWhiteSpace(failEvent!.TraceId));
        // No secrets in Detail (the request password must never appear).
        Assert.DoesNotContain("BrandNewP@ssword123", failEvent.Detail ?? string.Empty);
        Assert.DoesNotContain("OldPassword1!", failEvent.Detail ?? string.Empty);
    }
```

- [ ] **Step 2 — Run, expect FAIL** (Audit currently calls the legacy overload, so `AuditEvents` stays empty):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests.Production_InvalidCredentials_EmitsStructuredAuditWithTraceId"`
- [ ] **Step 3 — Minimal implementation.** Rewrite `Audit()` (lines 234-243) to compute traceId and emit a structured `AuditEvent`. Replace the method body:

```csharp
    private void Audit(string outcome, string username, string clientIp,
        SiemEventType? siemEvent = null, string? detail = null)
    {
        _logger.LogInformation(
            "PasswordChange outcome={Outcome} user={User} ip={Ip}",
            outcome, username, clientIp);

        if (siemEvent.HasValue)
        {
            var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "unknown";
            _siemService.LogEvent(new AuditEvent(
                EventType: siemEvent.Value,
                Outcome:   outcome,
                Username:  username,
                ClientIp:  clientIp,
                TraceId:   traceId,
                Detail:    detail));
        }
    }
```

  Add `using PassReset.Web.Services;` is already present (line 10). No caller signatures change — they already pass `(outcome, username, clientIp, siemEvent, detail)`. **Note:** `DistanceTooLow` (line 164) passes no `siemEvent`; leave it as-is (no SIEM event for that branch, preserving current behavior).

- [ ] **Step 4 — Run, expect PASS** (new test + the #28 `*_SiemRemainsGranular` tests must stay green — run both):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests"`
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/Controllers/PasswordController.cs src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs && git commit -m "feat(web): emit structured AuditEvent with traceId from Audit() [STAB-015]"`

### Task 30.3 — Emit PasswordChangeAttemptStarted at request entry

**Files:** `src/PassReset.Web/Controllers/PasswordController.cs` (PostAsync, after line 150)

- [ ] **Step 1 — Write failing test.** Add to `GenericErrorMappingTests.cs`:

```csharp
    [Fact]
    public async Task Post_EmitsPasswordChangeAttemptStarted_AtEntry()
    {
        using var factory = new ProductionEnvFactoryWithRecorder();
        using var client  = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        await client.PostAsJsonAsync("/api/password", MakeRequest("invalidCredentials"));

        Assert.Contains(SiemEventType.PasswordChangeAttemptStarted, factory.Recorder.Events);
    }
```

- [ ] **Step 2 — Run, expect FAIL:**
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests.Post_EmitsPasswordChangeAttemptStarted_AtEntry"`
- [ ] **Step 3 — Minimal implementation.** Insert an attempt-started audit immediately after `clientIp` is captured (after line 150, before the `ModelState.IsValid` check at 152):

```csharp
        Audit("AttemptStarted", model.Username, clientIp, SiemEventType.PasswordChangeAttemptStarted);
```

- [ ] **Step 4 — Run, expect PASS** (same command); then re-run the full mapping suite to confirm no regression: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests"`.
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/Controllers/PasswordController.cs src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs && git commit -m "feat(web): emit PasswordChangeAttemptStarted at request entry [STAB-015]"`

### Task 30.4 — Success-path structured audit test

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/PasswordControllerTests.cs` (new test) — verifies `PasswordChanged` structured event on success

- [ ] **Step 1 — Write failing test.** Add a self-contained test using a recording SIEM. Append to `PasswordControllerTests.cs` (reusing its existing factory patterns; mirror the `RecordingSiemService`/`SwapInRecordingSiem` approach — if the class lacks them, add a minimal local recorder). New test:

```csharp
    [Fact]
    public async Task Post_Success_EmitsStructuredPasswordChangedAudit()
    {
        var recorder = new RecordingSiemService();
        using var factory = new RecordingSiemFactory(recorder);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // "alice" is unknown to the debug provider → success path.
        var response = await client.PostAsJsonAsync("/api/password", MakeRequest("alice"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var changed = recorder.AuditEvents.FirstOrDefault(e => e.EventType == SiemEventType.PasswordChanged);
        Assert.NotNull(changed);
        Assert.Equal("Success", changed!.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(changed.TraceId));
    }
```

  Add the local `RecordingSiemService` (with `AuditEvents`) and a `RecordingSiemFactory` (Development env, debug provider, recording SIEM, lockout disabled) to `PasswordControllerTests.cs` if not already present, copying the structure from `GenericErrorMappingTests`.

- [ ] **Step 2 — Run, expect FAIL/discovery:**
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~PasswordControllerTests.Post_Success_EmitsStructuredPasswordChangedAudit"`
- [ ] **Step 3 — Minimal implementation.** None beyond 30.2 (success path already calls `Audit("Success", …, SiemEventType.PasswordChanged)` at line 204, now structured).
- [ ] **Step 4 — Run, expect PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/PasswordControllerTests.cs && git commit -m "test(web): assert structured PasswordChanged audit on success [STAB-015]"`

### Task 30.5 — Secrets-never-logged integration test

**Files:** `src/PassReset.Tests.Windows/Web/Services/AuditEventIntegrationTests.cs` *(new)*

- [ ] **Step 1 — Write failing test.** Create the file with a recording SIEM that captures every `AuditEvent`, POST validation-failed + invalid-credential requests, and assert no plaintext password appears in any `Detail`:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PassReset.Common;
using PassReset.PasswordProvider;
using PassReset.Web.Helpers;
using PassReset.Web.Models;
using PassReset.Web.Services;

namespace PassReset.Tests.Windows.Web.Services;

/// <summary>
/// STAB-015: end-to-end proof that no plaintext secret from a ChangePasswordModel ever
/// reaches the structured-audit Detail field, across both validation-failure and
/// auth-failure branches.
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
```

- [ ] **Step 2 — Run, expect FAIL** (file/test new; confirm discovery + green logic):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~AuditEventIntegrationTests"`
- [ ] **Step 3 — Minimal implementation.** None — the `Audit()` Detail comes only from sanitized `error.Message`/outcome strings.
- [ ] **Step 4 — Run, expect PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Services/AuditEventIntegrationTests.cs && git commit -m "test(web): prove audit Detail never carries plaintext secrets [STAB-015]"`

### Task 30.6 — Rate-limit 429 structured-audit regression guard

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs` (new test)

The `OnRejected` handler in `Program.cs` (line 407) emits `RateLimitExceeded` via the **legacy** overload. The acceptance criterion only requires the 429 be logged with the event type — which it is. Add a guard test proving a 429 emits `RateLimitExceeded`.

- [ ] **Step 1 — Write failing/guard test.** Add a recording-SIEM rate-limit factory + test:

```csharp
    private sealed class RecordingSiem : PassReset.Web.Services.ISiemService
    {
        public List<PassReset.Web.Services.SiemEventType> Events { get; } = new();
        public void LogEvent(PassReset.Web.Services.SiemEventType eventType, string username, string ipAddress, string? detail = null)
            => Events.Add(eventType);
        public void LogEvent(PassReset.Web.Services.AuditEvent evt) => Events.Add(evt.EventType);
    }

    private sealed class RecordingRateLimitFactory : WebApplicationFactory<Program>
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
                var existing = services.Where(d => d.ServiceType == typeof(PassReset.Web.Services.ISiemService)).ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddSingleton<PassReset.Web.Services.ISiemService>(Recorder);
            });
        }
    }

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
```

- [ ] **Step 2 — Run, expect PASS-or-FAIL.** Run: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~RateLimitAndRecaptchaTests.RateLimit_429_EmitsRateLimitExceededEvent"` — expect PASS (behavior already exists; this is a regression guard).
- [ ] **Step 3 — Minimal implementation.** None.
- [ ] **Step 4 — Confirm PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/RateLimitAndRecaptchaTests.cs && git commit -m "test(web): guard 429 emits RateLimitExceeded SIEM event [STAB-015]"`

### Task 30.7 — Document audit event types and fields

**Files:** `docs/Audit-Events.md` *(new)*

- [ ] **Step 1 — No test** (doc task; behavior locked by 30.1-30.6).
- [ ] **Step 2 — Verify code tests green** before writing docs:
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests|FullyQualifiedName~AuditEventIntegrationTests"`
- [ ] **Step 3 — Write `docs/Audit-Events.md`** with: (1) a table of every `SiemEventType` (PasswordChangeAttemptStarted, PasswordChanged, InvalidCredentials, UserNotFound, PortalLockout, ApproachingLockout, RateLimitExceeded, RecaptchaFailed, ChangeNotPermitted, ValidationFailed, Generic) and when each fires; (2) a table of `AuditEvent` fields (EventType, Outcome, Username, ClientIp, TraceId, Detail) with semantics, example values, and the redaction guarantee (no secret-named fields, enforced by `AuditEventRedactionTests`); (3) the legacy-vs-structured distinction (`LogEvent(SiemEventType,…)` vs `LogEvent(AuditEvent)`); (4) RFC 5424 STRUCTURED-DATA / SD-ID note (`SiemSettings.Syslog.SdId`, default `passreset@32473`) and how TraceId enables cross-log correlation; (5) guidance on `AlertEmail.AlertOnEvents`. Add a link to this file from `docs/appsettings-Production.md` §SiemSettings (one line).
- [ ] **Step 4 — Validate** the doc references real config keys: `npm run --prefix nul 2>nul` is not applicable — instead spot-check by grepping that `SdId` and `AlertOnEvents` exist: `dotnet build src/PassReset.sln -c Release` (ensures nothing referenced is fictional via the codebase staying consistent). Manual read-through.
- [ ] **Step 5 — Commit:**
  `git add docs/Audit-Events.md docs/appsettings-Production.md && git commit -m "docs(docs): document structured audit events and fields [STAB-015]"`

### Task 30.8 — Verify acceptance criteria & close #30

- [ ] Run: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~GenericErrorMappingTests|FullyQualifiedName~PasswordControllerTests|FullyQualifiedName~AuditEventIntegrationTests|FullyQualifiedName~RateLimitAndRecaptchaTests|FullyQualifiedName~AuditEventRedactionTests"` — all green.
- [ ] Confirm criteria: every attempt emits a correlation ID (TraceId) + structured event (incl. PasswordChangeAttemptStarted); secrets never logged (unit + integration); rate-limit and captcha blocks logged with event types; documentation describes event types and fields.
- [ ] Close: `gh issue close 30 --reason completed --comment "Closed by STAB-015: Audit() now emits structured AuditEvent records carrying Activity TraceId for every branch (added PasswordChangeAttemptStarted at entry), with integration proof that no plaintext secret reaches Detail, a guard that 429s emit RateLimitExceeded, and a new docs/Audit-Events.md reference. Builds on #28's SIEM-granularity invariant."`

---

## Issue #32 (STAB-016) — Installer HTTPS binding validation & output

**`risk: low`, `dependsOn: []`.** The app-level HSTS/redirect logic and `HttpsRedirectionTests.cs` already pass (no changes). The gap is installer-side validation + structured binding output. Pester tests run the pure function in dot-source mode (no live IIS).

### Task 32.1 — Add Test-HttpsBinding pure function (Pester-testable)

**Files:** `deploy/Install-PassReset.ps1` (add function after the helper block, near line 152); `deploy/Install-PassReset.Tests.ps1` (new Describe block)

- [ ] **Step 1 — Write failing Pester test.** Append to `Install-PassReset.Tests.ps1`:

```powershell
Describe 'Install-PassReset: Test-HttpsBinding' {
    It 'reports OK when an HTTPS binding exists on the target port' {
        $bindings = @(
            [pscustomobject]@{ protocol = 'https'; bindingInformation = '*:443:' }
        )
        $result = Test-HttpsBinding -Bindings $bindings -HttpsPort 443
        $result.HasHttps | Should -BeTrue
    }
    It 'reports missing when no HTTPS binding exists on the target port' {
        $bindings = @(
            [pscustomobject]@{ protocol = 'http'; bindingInformation = '*:80:' }
        )
        $result = Test-HttpsBinding -Bindings $bindings -HttpsPort 443
        $result.HasHttps | Should -BeFalse
    }
    It 'reports missing when HTTPS exists only on a different port' {
        $bindings = @(
            [pscustomobject]@{ protocol = 'https'; bindingInformation = '*:8443:' }
        )
        $result = Test-HttpsBinding -Bindings $bindings -HttpsPort 443
        $result.HasHttps | Should -BeFalse
    }
}
```

- [ ] **Step 2 — Run, expect FAIL** (function undefined):
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Test-HttpsBinding*'"`
- [ ] **Step 3 — Minimal implementation.** Add the pure function in `Install-PassReset.ps1` immediately after the `Abort` helper (after line 152). It takes a binding collection so it is testable without IIS; a thin wrapper at the call site supplies real bindings:

```powershell
# STAB-016: validate that an HTTPS binding exists on the configured port. Pure function
# (takes a binding collection) so Pester can exercise it without a live IIS site. Returns
# a small object the caller uses to Write-Ok / Write-Warn (warn-not-block per D-13).
function Test-HttpsBinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] $Bindings,
        [Parameter(Mandatory)] [int] $HttpsPort
    )
    $hasHttps = $false
    foreach ($b in $Bindings) {
        if ($b.protocol -eq 'https' -and $b.bindingInformation -match ":${HttpsPort}:") {
            $hasHttps = $true
            break
        }
    }
    return [pscustomobject]@{ HasHttps = $hasHttps; HttpsPort = $HttpsPort }
}
```

- [ ] **Step 4 — Run, expect PASS** (same command as Step 2).
- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add Test-HttpsBinding validation function [STAB-016]"`

### Task 32.2 — Wire Test-HttpsBinding + binding-config output block into the install flow

**Files:** `deploy/Install-PassReset.ps1` (after the HTTP-binding logic, lines ~1452-1455, before post-deploy verification at 1457)

- [ ] **Step 1 — Regression guard.** The HSTS tests are the app-side guard; run before editing:
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~HttpsRedirectionTests"` (expect PASS — unchanged).
- [ ] **Step 2 — (no new automated test for the IIS-side emission; it requires live IIS — covered by the pure-function Pester tests in 32.1 + manual verification in 32.4).**
- [ ] **Step 3 — Implementation.** Insert after line 1455 (after the `if ($CertThumbprint) { Write-Ok "...https... reachable..." }` block) and before the `# ----- STAB-019: post-deploy verification -----` comment:

```powershell
# STAB-016: validate binding/redirect consistency and emit a structured, secret-free
# final binding configuration block (warn-not-block per D-13).
$allBindings = @(Get-IISSiteBinding -Name $SiteName -ErrorAction SilentlyContinue) |
    ForEach-Object {
        [pscustomobject]@{ protocol = $_.Protocol; bindingInformation = $_.BindingInformation }
    }
$httpsCheck = Test-HttpsBinding -Bindings $allBindings -HttpsPort $HttpsPort

Write-Host "`n*** FINAL BINDING CONFIGURATION ***" -ForegroundColor Cyan
foreach ($b in $allBindings) {
    $port = ($b.bindingInformation -split ':')[1]
    if ($b.protocol -eq 'https') {
        $thumbShort = if ($CertThumbprint) { $CertThumbprint.Substring(0, [Math]::Min(8, $CertThumbprint.Length)) } else { '(none)' }
        Write-Ok "Binding: protocol=https, port=$port, host=* (cert thumbprint: $thumbShort...)"
    } else {
        Write-Ok "Binding: protocol=http,  port=$port, host=* (HTTP->HTTPS redirect target: https://${hostHeader}:${HttpsPort})"
    }
}

if ($httpsCheck.HasHttps) {
    Write-Ok "HTTPS binding verified on ${SiteName}:${HttpsPort}"
} else {
    Write-Warn "HTTPS binding missing on ${SiteName}:${HttpsPort} — UseHttpsRedirection() will redirect to a non-existent binding"
}

# Recommend EnableHttpsRedirect when a cert is bound (best-effort read of live config).
if ($CertThumbprint) {
    $prodCfgPath = Join-Path $InstallPath 'appsettings.Production.json'
    $redirectOn = $null
    if (Test-Path $prodCfgPath) {
        try {
            $prodCfg = Get-Content $prodCfgPath -Raw | ConvertFrom-Json
            $redirectOn = $prodCfg.WebSettings.EnableHttpsRedirect
        } catch { $redirectOn = $null }
    }
    if ($redirectOn -ne $true) {
        Write-Warn "Certificate bound but WebSettings:EnableHttpsRedirect is not 'true' in appsettings.Production.json — set it to enable HTTP->HTTPS redirect and HSTS (will be applied during config sync if missing)."
    } else {
        Write-Ok "EnableHttpsRedirect=true — HTTP->HTTPS redirect and HSTS active"
    }
}
```

  (Verify `$InstallPath` is the variable name used elsewhere in the script for the deploy directory; if the script uses a different name e.g. `$TargetPath`, substitute it — grep before editing.)

- [ ] **Step 4 — Validate syntax** (no live IIS needed):
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"` (all green — confirms the script still dot-sources without parse errors after the edit).
- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.ps1 && git commit -m "feat(installer): emit final binding config + validate HTTPS consistency [STAB-016]"`

### Task 32.3 — Confirm the `$InstallPath` / host-header variable name is correct

**Files:** `deploy/Install-PassReset.ps1` (no functional change unless mismatch found)

- [ ] **Step 1 — Verify.** Grep for the deploy-directory variable used near config sync: `Grep "appsettings.Production.json" deploy/Install-PassReset.ps1` and confirm the variable used in 32.2 matches the script's actual path variable (`$InstallPath`, `$TargetDir`, or similar) and that `$hostHeader` is in scope at the insertion point (it is set at line 1434).
- [ ] **Step 2 — If a mismatch exists**, fix the variable name in the 32.2 block to match.
- [ ] **Step 3 — Re-run Pester** to confirm parse integrity:
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"`
- [ ] **Step 4 — N/A** (covered by Step 3).
- [ ] **Step 5 — Commit only if a fix was needed:**
  `git add deploy/Install-PassReset.ps1 && git commit -m "fix(installer): correct path variable in binding-config block [STAB-016]"`

### Task 32.4 — Verify acceptance criteria & close #32

- [ ] Run: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~HttpsRedirectionTests"` (3 tests green) **and** `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"` (all green incl. Test-HttpsBinding).
- [ ] Confirm criteria: installer can enforce/validate HTTPS-only or HTTP→HTTPS redirect (Test-HttpsBinding warns on missing binding); HSTS consistency recommended (EnableHttpsRedirect warning); installer outputs final binding configuration (host/port/protocol, cert thumbprint short-form, no secrets).
- [ ] Close: `gh issue close 32 --reason completed --comment "Closed by STAB-016: added Pester-tested Test-HttpsBinding (warn-not-block per D-13), a structured *** FINAL BINDING CONFIGURATION *** output block (protocol/port/host + 8-char cert thumbprint, no secrets), and an EnableHttpsRedirect consistency recommendation. App-side HSTS/redirect HttpsRedirectionTests remain green."`

---

## Issue #33 (STAB-017) — Secret env-var override coverage & doc accuracy

**`risk: low`, `dependsOn: []`.** Config keys confirmed: `PasswordChangeOptions__LdapPassword` and `PasswordChangeOptions__ServiceAccountPassword`; both `[JsonIgnore]` on `PasswordChangeOptions`. The `EnvironmentVariableOverrideTests` belong to the serial `EnvVarSerial` collection.

### Task 33.1 — Env-var override test for LdapPassword

**Files:** `src/PassReset.Tests.Windows/Web/Startup/EnvironmentVariableOverrideTests.cs` (add test + bind PasswordChangeOptions in EnvVarFactory)

- [ ] **Step 1 — Write failing test.** Append to the class:

```csharp
    [Fact]
    public void EnvVar_LdapPassword_OverridesAppsettings()
    {
        SetEnv("PasswordChangeOptions__LdapPassword", "ldap-from-env");

        using var factory = new EnvVarFactory();
        var options = factory.Services.GetRequiredService<IOptions<PasswordChangeOptions>>();

        Assert.Equal("ldap-from-env", options.Value.LdapPassword);
    }
```

  Add `using PassReset.Common;` to the test file's usings (the others use `PassReset.Web.Models`). The `EnvVarFactory` already seeds `PasswordChangeOptions:UseAutomaticContext`, so the section binds.

- [ ] **Step 2 — Run, expect FAIL or PASS.** Run: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~EnvironmentVariableOverrideTests.EnvVar_LdapPassword_OverridesAppsettings"`. (Expect PASS — ASP.NET Core's default env-var source already binds `__` keys; this is a documented-contract guard. If it FAILS, investigate binding order.)
- [ ] **Step 3 — Minimal implementation.** None expected — STAB-017 env-var precedence is already wired (Program.cs:197). If the test fails, the fix is to ensure `PasswordChangeOptions` is bound from configuration (it is, via `AddOptions`/`Configure`).
- [ ] **Step 4 — Confirm PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Startup/EnvironmentVariableOverrideTests.cs && git commit -m "test(web): prove LdapPassword env-var override [STAB-017]"`

### Task 33.2 — Env-var override test for ServiceAccountPassword

**Files:** `src/PassReset.Tests.Windows/Web/Startup/EnvironmentVariableOverrideTests.cs`

- [ ] **Step 1 — Write failing test:**

```csharp
    [Fact]
    public void EnvVar_ServiceAccountPassword_OverridesAppsettings()
    {
        SetEnv("PasswordChangeOptions__ServiceAccountPassword", "svc-from-env");

        using var factory = new EnvVarFactory();
        var options = factory.Services.GetRequiredService<IOptions<PasswordChangeOptions>>();

        Assert.Equal("svc-from-env", options.Value.ServiceAccountPassword);
    }
```

- [ ] **Step 2 — Run, expect PASS** (documented-contract guard):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~EnvironmentVariableOverrideTests.EnvVar_ServiceAccountPassword_OverridesAppsettings"`
- [ ] **Step 3 — Minimal implementation.** None expected.
- [ ] **Step 4 — Confirm PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Startup/EnvironmentVariableOverrideTests.cs && git commit -m "test(web): prove ServiceAccountPassword env-var override [STAB-017]"`

### Task 33.3 — Health endpoint no-leak sentinels for LDAP secrets

**Files:** `src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs` (sentinels at 26-27; DebugFactory config at 239-244; assertions at 122-132)

- [ ] **Step 1 — Write failing test.** Add two sentinel constants (after line 27):

```csharp
    private const string LdapPasswordSentinel = "TEST_LDAP_DO_NOT_LEAK";
    private const string ServiceAccountPasswordSentinel = "TEST_SVCACCT_DO_NOT_LEAK";
```

  Seed them in `DebugFactory.ConfigureWebHost` (add to the dictionary after line 244):

```csharp
                    ["PasswordChangeOptions:LdapPassword"]            = LdapPasswordSentinel,
                    ["PasswordChangeOptions:ServiceAccountPassword"]  = ServiceAccountPasswordSentinel,
```

  Extend `Health_Body_ContainsNoSecrets` (after line 131):

```csharp
        Assert.DoesNotContain(LdapPasswordSentinel, body);
        Assert.DoesNotContain(ServiceAccountPasswordSentinel, body);
```

- [ ] **Step 2 — Run, expect PASS** (regression guard — `[JsonIgnore]` on both properties already prevents leakage):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~HealthControllerTests.Health_Body_ContainsNoSecrets"`
- [ ] **Step 3 — Minimal implementation.** None — `LdapPassword`/`ServiceAccountPassword` are `[JsonIgnore]` (PasswordChangeOptions.cs:116,142). If the test were to fail it would reveal a real leak; the guard locks the contract.
- [ ] **Step 4 — Confirm PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs && git commit -m "test(web): guard health output never leaks LDAP secrets [STAB-017]"`

### Task 33.4 — Correct stale documentation (Secret-Management.md:65)

**Files:** `docs/Secret-Management.md` (line 65)

- [ ] **Step 1 — No automated test** (doc accuracy task).
- [ ] **Step 2 — Confirm installer behavior** (the source of truth): the installer DOES set env vars via `-LdapPassword`/`-SmtpPassword`/`-RecaptchaPrivateKey` → `Set-PoolEnvVar` (Install-PassReset.ps1:1670-1686). The doc claim "installer does NOT set these environment variables" is false.
- [ ] **Step 3 — Edit line 65.** Replace:

```markdown
**Operator note:** The installer SUPPORTS setting these environment variables at install time via the `-LdapPassword`, `-SmtpPassword`, and `-RecaptchaPrivateKey` parameters (each a `[SecureString]`), which call `Set-PoolEnvVar` to write the value into the AppPool's `environmentVariables` collection idempotently (existing values are never overwritten). Operators MAY instead inject secrets manually post-install via IIS Manager, `appcmd`, or `dotnet user-secrets` — both paths are valid; the operator owns the choice. Full encrypted-at-rest solutions (DPAPI/Key Vault) are tracked separately (V2-003); STAB-017 is the documented env-var stepping stone.
```

- [ ] **Step 4 — Cross-check** the parameter names against the installer param block (Install-PassReset.ps1:98-100) to ensure the doc names match exactly.
- [ ] **Step 5 — Commit:**
  `git add docs/Secret-Management.md && git commit -m "docs(docs): correct installer secret env-var behavior [STAB-017]"`

### Task 33.5 — Verify acceptance criteria & close #33

- [ ] Run: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~EnvironmentVariableOverrideTests|FullyQualifiedName~HealthControllerTests"` — all green.
- [ ] Confirm criteria: precedence/IIS configuration documented accurately; installer supports setting/referencing env vars for the AppPool identity (now test-gated); no secrets in logs or health output (LDAP sentinels added).
- [ ] Close: `gh issue close 33 --reason completed --comment "Closed by STAB-017: added EnvVar_LdapPassword_OverridesAppsettings + EnvVar_ServiceAccountPassword_OverridesAppsettings (PasswordChangeOptions__* precedence), extended Health_Body_ContainsNoSecrets with LDAP/service-account sentinels, and corrected the stale docs/Secret-Management.md line 65 to reflect that the installer DOES set env vars via -LdapPassword/-SmtpPassword/-RecaptchaPrivateKey."`

---

## Issue #36 (STAB-004) — Map E_ACCESSDENIED from UnauthorizedAccessException

**Sequencing:** depends on #27 schema. **`risk: low`, `dependsOn: [27]`.** `AuthenticablePrincipal.ChangePassword` can surface `UnauthorizedAccessException` (wrapping E_ACCESSDENIED) which currently falls through to the generic catch (PasswordChangeProvider.cs:195) → `ApiErrorCode.Generic`. We extract a pure, testable HResult-classification helper (mirroring the existing static `EvaluateMinPwdAge` pattern) so we can unit-test without a live AD principal, then add the `UnauthorizedAccessException` catch.

### Task 36.1 — Extract pure ClassifyChangePasswordHResult helper + test it

**Files:** `src/PassReset.PasswordProvider/PasswordChangeProvider.cs` (ChangePasswordInternal, lines 478-528); `src/PassReset.Tests.Windows/PasswordProvider/ChangePasswordHResultTests.cs` *(new)*

- [ ] **Step 1 — Write failing test.** Create `ChangePasswordHResultTests.cs`:

```csharp
using PassReset.Common;
using PassReset.PasswordProvider;

namespace PassReset.Tests.Windows.PasswordProvider;

/// <summary>
/// STAB-004: the HResult classifier maps E_ACCESSDENIED / DS_CONSTRAINT_VIOLATION to
/// PasswordTooRecentlyChanged regardless of whether the exception arrived as a COMException
/// or an UnauthorizedAccessException. Pure helper → no live AD needed.
/// </summary>
public class ChangePasswordHResultTests
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int ERROR_DS_CONSTRAINT_VIOLATION = unchecked((int)0x8007202F);

    [Fact]
    public void AccessDenied_MapsToPasswordTooRecentlyChanged()
    {
        var code = PasswordChangeProvider.ClassifyChangePasswordHResult(E_ACCESSDENIED);
        Assert.Equal(ApiErrorCode.PasswordTooRecentlyChanged, code);
    }

    [Fact]
    public void ConstraintViolation_MapsToPasswordTooRecentlyChanged()
    {
        var code = PasswordChangeProvider.ClassifyChangePasswordHResult(ERROR_DS_CONSTRAINT_VIOLATION);
        Assert.Equal(ApiErrorCode.PasswordTooRecentlyChanged, code);
    }

    [Fact]
    public void UnknownHResult_MapsToNull()
    {
        var code = PasswordChangeProvider.ClassifyChangePasswordHResult(unchecked((int)0x80070057)); // E_INVALIDARG
        Assert.Null(code);
    }
}
```

- [ ] **Step 2 — Run, expect FAIL** (helper does not exist):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~ChangePasswordHResultTests"`
- [ ] **Step 3 — Minimal implementation.** Add the pure static helper to `PasswordChangeProvider.cs` (place it just above `ChangePasswordInternal`, before line 478). Make it `public static` (the existing `EvaluateMinPwdAge` is `public static`, used the same way by tests):

```csharp
    /// <summary>
    /// STAB-004: classifies an HRESULT raised during ChangePassword into an
    /// <see cref="ApiErrorCode"/>. Returns <c>null</c> when the HRESULT is not one of the
    /// known policy-violation codes (caller then preserves existing fallback behavior).
    /// Shared by both the COMException and UnauthorizedAccessException catch blocks so the
    /// mapping is identical regardless of which exception type AccountManagement surfaces.
    /// </summary>
    public static ApiErrorCode? ClassifyChangePasswordHResult(int hresult)
    {
        const int E_ACCESSDENIED = unchecked((int)0x80070005);
        const int ERROR_DS_CONSTRAINT_VIOLATION = unchecked((int)0x8007202F);
        return hresult is E_ACCESSDENIED or ERROR_DS_CONSTRAINT_VIOLATION
            ? ApiErrorCode.PasswordTooRecentlyChanged
            : null;
    }
```

  Refactor the existing COMException block (lines 488-504) to use it (keeps behavior identical):

```csharp
            var classified = ClassifyChangePasswordHResult(comEx.HResult);
            if (classified is not null)
            {
                ExceptionChainLogger.LogExceptionChain(_logger, comEx,
                    "AD rejected ChangePassword for {User} with HRESULT=0x{Hex:X8}; message={Message}. " +
                    "Treating as minimum-password-age violation. If this user IS allowed to change password, " +
                    "verify the service account has the 'Change Password' extended right.",
                    userPrincipal.SamAccountName,
                    comEx.HResult,
                    comEx.Message);

                throw new ApiErrorException(
                    "Your password was changed too recently. Please wait before trying again.",
                    classified.Value);
            }
```

- [ ] **Step 4 — Run, expect PASS** + regression-check the existing provider tests:
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~ChangePasswordHResultTests|FullyQualifiedName~PreCheckMinPwdAgeTests"`
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.PasswordProvider/PasswordChangeProvider.cs src/PassReset.Tests.Windows/PasswordProvider/ChangePasswordHResultTests.cs && git commit -m "refactor(provider): extract pure ChangePasswordHResult classifier [STAB-004]"`

### Task 36.2 — Add UnauthorizedAccessException catch in ChangePasswordInternal

**Files:** `src/PassReset.PasswordProvider/PasswordChangeProvider.cs` (ChangePasswordInternal, after the COMException catch, ~line 528)

- [ ] **Step 1 — Write failing test.** Because `ChangePasswordInternal` requires an `AuthenticablePrincipal` (not mockable), test the **observable contract** via the catch block's effect using a thin seam: add a `public static` test hook that runs the same classification + throw logic the new catch uses, OR test that the new catch path produces `PasswordTooRecentlyChanged`. The cleanest TDD-able unit is a small static method `MapUnauthorizedAccess` that the catch delegates to. Add the test:

```csharp
    [Fact]
    public void MapUnauthorizedAccess_AccessDenied_ThrowsPasswordTooRecentlyChanged()
    {
        var ex = new UnauthorizedAccessException("denied")
        { HResult = unchecked((int)0x80070005) };

        var thrown = Assert.Throws<ApiErrorException>(() =>
            PasswordChangeProvider.MapUnauthorizedAccess(ex));
        Assert.Equal(ApiErrorCode.PasswordTooRecentlyChanged, thrown.ErrorCode);
    }

    [Fact]
    public void MapUnauthorizedAccess_PermissionDenied_Rethrows()
    {
        // A non-policy HResult must NOT be swallowed as a min-age violation — rethrow so the
        // outer generic catch produces the permission-issue diagnostic path.
        var ex = new UnauthorizedAccessException("genuine permission failure")
        { HResult = unchecked((int)0x80070057) };

        Assert.Throws<UnauthorizedAccessException>(() =>
            PasswordChangeProvider.MapUnauthorizedAccess(ex));
    }
```

  (Add these to `ChangePasswordHResultTests.cs`.)

- [ ] **Step 2 — Run, expect FAIL** (method missing):
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~ChangePasswordHResultTests.MapUnauthorizedAccess"`
- [ ] **Step 3 — Minimal implementation.** Add the `MapUnauthorizedAccess` static helper near `ClassifyChangePasswordHResult` — but it needs the logger for the diagnostic chain. Make it an instance method that the catch calls, and expose a `public static` overload for testing that takes no logger (logs are a side effect, not the contract). Implement as:

```csharp
    /// <summary>
    /// STAB-004: classifies an <see cref="UnauthorizedAccessException"/> wrapping E_ACCESSDENIED.
    /// Throws <see cref="ApiErrorException"/>(PasswordTooRecentlyChanged) for policy-violation
    /// HResults; rethrows the original for genuine permission failures so the outer catch logs
    /// the permission diagnostic. Static + logger-free so it is unit-testable.
    /// </summary>
    public static void MapUnauthorizedAccess(UnauthorizedAccessException ex)
    {
        var classified = ClassifyChangePasswordHResult(ex.HResult);
        if (classified is not null)
            throw new ApiErrorException(
                "Your password was changed too recently. Please wait before trying again.",
                classified.Value);
        throw ex;
    }
```

  Add the catch block in `ChangePasswordInternal` immediately after the COMException catch closes (after line 528, before the method's closing brace):

```csharp
        catch (UnauthorizedAccessException uaEx)
        {
            // STAB-004: AccountManagement can surface E_ACCESSDENIED as UnauthorizedAccessException
            // (not COMException). Map policy HResults to PasswordTooRecentlyChanged with the same
            // diagnostic chain as the COMException path; rethrow genuine permission failures.
            ExceptionChainLogger.LogExceptionChain(_logger, uaEx,
                "ChangePassword raised UnauthorizedAccessException for {User} HRESULT=0x{Hex:X8}; " +
                "treating E_ACCESSDENIED as minimum-password-age violation, otherwise a permission issue.",
                userPrincipal.SamAccountName, uaEx.HResult);
            MapUnauthorizedAccess(uaEx);
        }
```

- [ ] **Step 4 — Run, expect PASS** + full provider regression:
  `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~ChangePasswordHResultTests|FullyQualifiedName~PreCheckMinPwdAgeTests"`
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.PasswordProvider/PasswordChangeProvider.cs src/PassReset.Tests.Windows/PasswordProvider/ChangePasswordHResultTests.cs && git commit -m "fix(provider): map E_ACCESSDENIED UnauthorizedAccessException to PasswordTooRecentlyChanged [STAB-004]"`

### Task 36.3 — Verify acceptance criteria & close #36

- [ ] Run: `dotnet test src/PassReset.Tests.Windows --filter "FullyQualifiedName~ChangePasswordHResultTests|FullyQualifiedName~PreCheckMinPwdAgeTests"` — all green; then full provider build: `dotnet build src/PassReset.sln -c Release`.
- [ ] Confirm criteria: E_ACCESSDENIED is categorized into PasswordTooRecentlyChanged (not Generic) whether raised as COMException or UnauthorizedAccessException; logs carry HResult + interpretation via ExceptionChainLogger for both paths; consecutive-change attempts no longer surface a generic "unexpected error" when mappable.
- [ ] Close: `gh issue close 36 --reason completed --comment "Closed by STAB-004: extracted the pure ClassifyChangePasswordHResult classifier (shared by the COMException path) and added an UnauthorizedAccessException catch in ChangePasswordInternal that maps E_ACCESSDENIED/DS_CONSTRAINT_VIOLATION to PasswordTooRecentlyChanged with the same ExceptionChainLogger diagnostics, rethrowing genuine permission failures. Unit-tested via ChangePasswordHResultTests."`

---

## Issue #38 (STAB-021) — Full password policy display (LDAP complexity/history + UI age)

**Sequencing:** depends on #27 schema. **`risk: medium`, `dependsOn: [27]`.** Two independent surfaces: LDAP backend (C#/xUnit) and React UI (Vitest). Regression risk on the backend: the LDAP policy method currently returns `RequiresComplexity:false, HistoryLength:0`. Adding a second LDAP search must not break `GetEffectivePasswordPolicyAsync_NoRootDse_ReturnsNull` or `_ReturnsPolicyFromRootDse`. **What could break:** the new domain-root search throwing must be caught and degrade to `false/0` (not null), preserving the existing "never throws" contract. We wrap the second search in its own try/catch.

### Task 38.1 — Add pwdProperties / pwdHistoryLength / defaultNamingContext attribute constants

**Files:** `src/PassReset.PasswordProvider.Ldap/LdapAttributeNames.cs`

- [ ] **Step 1 — Write failing test.** Add to `LdapPasswordChangeProviderTests.cs` a test that drives the as-yet-unimplemented domain-root read (it will fail until 38.2). Add:

```csharp
    [Fact]
    public async Task GetEffectivePasswordPolicyAsync_ReadsComplexityFromDomainRoot()
    {
        var (sut, fake) = Build();
        fake.RootDse = MakeEntry("",
            (LdapAttributeNames.MinPwdLength, "8"),
            ("defaultNamingContext", "DC=corp,DC=example,DC=com"));
        // Domain-root object carries pwdProperties (0x1 = complexity) and pwdHistoryLength.
        fake.OnSearch("objectClass=domainDNS",
            MakeResponse(MakeEntry("DC=corp,DC=example,DC=com",
                ("pwdProperties", "1"),
                ("pwdHistoryLength", "24"))));

        var policy = await sut.GetEffectivePasswordPolicyAsync();

        Assert.NotNull(policy);
        Assert.True(policy!.RequiresComplexity);
        Assert.Equal(24, policy.HistoryLength);
    }
```

- [ ] **Step 2 — Run, expect FAIL** (constants + impl missing):
  `dotnet test src/PassReset.Tests --filter "FullyQualifiedName~LdapPasswordChangeProviderTests.GetEffectivePasswordPolicyAsync_ReadsComplexityFromDomainRoot"`
- [ ] **Step 3 — Minimal implementation.** Add the constants to `LdapAttributeNames.cs` (after line 24):

```csharp
    public const string PwdProperties        = "pwdProperties";
    public const string PwdHistoryLength     = "pwdHistoryLength";
    public const string DefaultNamingContext = "defaultNamingContext";
```

  (Implementation that consumes them lands in 38.2 — this task only adds the names so the test references compile. The test stays red until 38.2.)

- [ ] **Step 4 — Run, expect still FAIL** (impl not yet done) — confirm it now compiles and fails on the assertion, not a missing symbol:
  `dotnet test src/PassReset.Tests --filter "FullyQualifiedName~LdapPasswordChangeProviderTests.GetEffectivePasswordPolicyAsync_ReadsComplexityFromDomainRoot"`
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.PasswordProvider.Ldap/LdapAttributeNames.cs src/PassReset.Tests/Services/LdapPasswordChangeProviderTests.cs && git commit -m "test(provider): add domain-root policy attrs + failing complexity test [STAB-021]"`

### Task 38.2 — Implement domain-root pwdProperties/pwdHistoryLength read

**Files:** `src/PassReset.PasswordProvider.Ldap/LdapPasswordChangeProvider.cs` (GetEffectivePasswordPolicyAsync, lines 654-699)

- [ ] **Step 1 — Test already written** (38.1's `_ReadsComplexityFromDomainRoot`, plus add the history-specific test now):

```csharp
    [Fact]
    public async Task GetEffectivePasswordPolicyAsync_NoComplexityBit_ReportsFalse()
    {
        var (sut, fake) = Build();
        fake.RootDse = MakeEntry("",
            (LdapAttributeNames.MinPwdLength, "8"),
            ("defaultNamingContext", "DC=corp,DC=example,DC=com"));
        fake.OnSearch("objectClass=domainDNS",
            MakeResponse(MakeEntry("DC=corp,DC=example,DC=com",
                ("pwdProperties", "0"),     // complexity bit NOT set
                ("pwdHistoryLength", "0"))));

        var policy = await sut.GetEffectivePasswordPolicyAsync();

        Assert.NotNull(policy);
        Assert.False(policy!.RequiresComplexity);
        Assert.Equal(0, policy.HistoryLength);
    }
```

- [ ] **Step 2 — Run, expect FAIL:**
  `dotnet test src/PassReset.Tests --filter "FullyQualifiedName~LdapPasswordChangeProviderTests.GetEffectivePasswordPolicyAsync_ReadsComplexityFromDomainRoot|FullyQualifiedName~LdapPasswordChangeProviderTests.GetEffectivePasswordPolicyAsync_NoComplexityBit_ReportsFalse"`
- [ ] **Step 3 — Minimal implementation.** Replace the hard-coded `RequiresComplexity: false, HistoryLength: 0` (lines 687-692) with a domain-root read. Insert before the `return` and update it:

```csharp
            // Read complexity + history from the domain root object. These live on the
            // domainDNS object (pwdProperties bitmask, pwdHistoryLength), not on rootDSE.
            // Best-effort: any failure degrades to false/0 so this method never throws.
            bool requiresComplexity = false;
            int historyLength = 0;
            var domainDn = GetFirstStringValueOrNull(rootDse, LdapAttributeNames.DefaultNamingContext);
            if (!string.IsNullOrWhiteSpace(domainDn))
            {
                try
                {
                    var rootReq = new SearchRequest(
                        distinguishedName: domainDn,
                        ldapFilter: "(objectClass=domainDNS)",
                        searchScope: SearchScope.Base,
                        attributeList: new[] { LdapAttributeNames.PwdProperties, LdapAttributeNames.PwdHistoryLength });
                    var rootResp = session.Search(rootReq);
                    if (rootResp.Entries.Count > 0)
                    {
                        var domainEntry = rootResp.Entries[0];
                        if (long.TryParse(GetFirstStringValueOrNull(domainEntry, LdapAttributeNames.PwdProperties), out var pwdProps))
                            requiresComplexity = (pwdProps & 0x1) != 0; // DOMAIN_PASSWORD_COMPLEX
                        if (int.TryParse(GetFirstStringValueOrNull(domainEntry, LdapAttributeNames.PwdHistoryLength), out var hist))
                            historyLength = hist;
                    }
                }
                catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
                {
                    _logger.LogWarning(ex,
                        "Domain-root pwdProperties/pwdHistoryLength read failed; reporting complexity=false history=0");
                }
            }

            return Task.FromResult<PasswordPolicy?>(
                new PasswordPolicy(minLen, requiresComplexity, historyLength, minAgeDays, maxAgeDays));
```

  Update the method XML-doc (lines 647-652) to remove the "reported as false/0 here" claim and note the domain-root read. Note the FGPP gap remains documented (see Task 38.5).

- [ ] **Step 4 — Run, expect PASS** + regression on the existing two policy tests:
  `dotnet test src/PassReset.Tests --filter "FullyQualifiedName~LdapPasswordChangeProviderTests.GetEffectivePasswordPolicyAsync"`
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.PasswordProvider.Ldap/LdapPasswordChangeProvider.cs && git commit -m "feat(provider): read complexity/history from domain root in LDAP policy [STAB-021]"`

### Task 38.3 — Regression test: domain-root search failure degrades gracefully

**Files:** `src/PassReset.Tests/Services/LdapPasswordChangeProviderTests.cs`

- [ ] **Step 1 — Write failing/guard test** (proves the new search throwing never breaks the existing contract):

```csharp
    [Fact]
    public async Task GetEffectivePasswordPolicyAsync_DomainRootSearchThrows_StillReturnsPolicy()
    {
        var (sut, fake) = Build();
        fake.RootDse = MakeEntry("",
            (LdapAttributeNames.MinPwdLength, "10"),
            ("defaultNamingContext", "DC=corp,DC=example,DC=com"));
        fake.OnSearchThrow("objectClass=domainDNS",
            new DirectoryOperationException("simulated domain-root failure"));

        var policy = await sut.GetEffectivePasswordPolicyAsync();

        // Must NOT return null or throw — degrades to false/0 for the two domain-root fields.
        Assert.NotNull(policy);
        Assert.Equal(10, policy!.MinLength);
        Assert.False(policy.RequiresComplexity);
        Assert.Equal(0, policy.HistoryLength);
    }
```

- [ ] **Step 2 — Run, expect PASS** (the try/catch in 38.2 handles it — this guards against future refactors removing the catch):
  `dotnet test src/PassReset.Tests --filter "FullyQualifiedName~LdapPasswordChangeProviderTests.GetEffectivePasswordPolicyAsync_DomainRootSearchThrows_StillReturnsPolicy"`
- [ ] **Step 3 — Minimal implementation.** None (covered by 38.2's catch).
- [ ] **Step 4 — Confirm PASS** (same command).
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Tests/Services/LdapPasswordChangeProviderTests.cs && git commit -m "test(provider): guard LDAP policy degrades on domain-root failure [STAB-021]"`

### Task 38.4 — Render minAgeDays / maxAgeDays in AdPasswordPolicyPanel (Vitest)

**Files:** `src/PassReset.Web/ClientApp/src/components/AdPasswordPolicyPanel.tsx` (lines 24-30); `src/PassReset.Web/ClientApp/src/components/__tests__/AdPasswordPolicyPanel.test.tsx`

- [ ] **Step 1 — Write failing tests.** Add to the `describe('AdPasswordPolicyPanel', …)` block:

```tsx
  it('renders minimum age when minAgeDays > 0 (renders_minAgeDays_when_greater_than_zero)', () => {
    render(<AdPasswordPolicyPanel policy={{ ...samplePolicy, minAgeDays: 1, maxAgeDays: 0 }} loading={false} />);
    expect(screen.getByText(/minimum age/i)).toBeInTheDocument();
    expect(screen.getByText(/1 day/i)).toBeInTheDocument();
  });

  it('renders expiry when maxAgeDays > 0 (renders_maxAgeDays_when_greater_than_zero)', () => {
    render(<AdPasswordPolicyPanel policy={{ ...samplePolicy, minAgeDays: 0, maxAgeDays: 90 }} loading={false} />);
    expect(screen.getByText(/expires/i)).toBeInTheDocument();
    expect(screen.getByText(/90/)).toBeInTheDocument();
  });

  it('hides age constraints when both are zero (hides_age_constraints_when_zero)', () => {
    render(<AdPasswordPolicyPanel policy={{ ...samplePolicy, minAgeDays: 0, maxAgeDays: 0 }} loading={false} />);
    expect(screen.queryByText(/minimum age/i)).toBeNull();
    expect(screen.queryByText(/expires/i)).toBeNull();
  });
```

- [ ] **Step 2 — Run, expect FAIL:**
  `cd src/PassReset.Web/ClientApp && npm test -- --run AdPasswordPolicyPanel`
- [ ] **Step 3 — Minimal implementation.** In `AdPasswordPolicyPanel.tsx`, after the `historyLength` block (after line 30, before the `return`):

```tsx
  if (policy.minAgeDays > 0) {
    rules.push(`Minimum age: ${policy.minAgeDays} day(s) before it can be changed again`);
  }
  if (policy.maxAgeDays > 0) {
    rules.push(`Password expires after ${policy.maxAgeDays} days`);
  }
```

- [ ] **Step 4 — Run, expect PASS** + full panel suite (the existing 5 tests must stay green):
  `cd src/PassReset.Web/ClientApp && npm test -- --run AdPasswordPolicyPanel`
- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/ClientApp/src/components/AdPasswordPolicyPanel.tsx src/PassReset.Web/ClientApp/src/components/__tests__/AdPasswordPolicyPanel.test.tsx && git commit -m "feat(web): display password min/max age in policy panel [STAB-021]"`

### Task 38.5 — Document password-policy display behavior, LDAP limits, FGPP scope

**Files:** `docs/Password-Policy.md` *(new)*

- [ ] **Step 1 — No automated test** (doc task; behavior locked by 38.1-38.4).
- [ ] **Step 2 — Verify code green** before docs:
  `dotnet test src/PassReset.Tests --filter "FullyQualifiedName~LdapPasswordChangeProviderTests.GetEffectivePasswordPolicyAsync"` and `cd src/PassReset.Web/ClientApp && npm test -- --run AdPasswordPolicyPanel`.
- [ ] **Step 3 — Write `docs/Password-Policy.md`** covering: (1) which fields the panel displays — minLength, complexity, history, minAgeDays, maxAgeDays — and their meaning (`minAgeDays`: days before a password can be changed again; `maxAgeDays`: expiry window, 0 = no expiry); (2) LDAP provider now reads complexity/history from the domain root (`pwdProperties` bit 0x1, `pwdHistoryLength`) via a Base-scope `(objectClass=domainDNS)` search on `defaultNamingContext`, degrading to false/0 on failure; (3) FGPP (Fine-Grained Password Policy) is **not yet supported** in either provider — the displayed policy is always the domain default; link to the issue tracker for the FGPP follow-up; (4) note that minPwdAge is also enforced server-side (`PreCheckMinPwdAge`) so the panel value warns users before they attempt a too-soon change. Add a one-line cross-link from `docs/appsettings-Production.md` (or `README.md` policy section).
- [ ] **Step 4 — Manual read-through** + ensure referenced config/attribute names match the code (`pwdProperties`, `pwdHistoryLength`, `defaultNamingContext`).
- [ ] **Step 5 — Commit:**
  `git add docs/Password-Policy.md docs/appsettings-Production.md && git commit -m "docs(docs): document password policy display and FGPP scope [STAB-021]"`

### Task 38.6 — Verify acceptance criteria & close #38

- [ ] Backend: `dotnet test src/PassReset.Tests --filter "FullyQualifiedName~LdapPasswordChangeProviderTests"` — green (incl. new complexity/history/degrade tests).
- [ ] Frontend: `cd src/PassReset.Web/ClientApp && npm test -- --run AdPasswordPolicyPanel` — all 8 tests green.
- [ ] Confirm criteria: backend determines effective policy (domain default complexity/history now read; FGPP documented as out-of-scope); API exposes the normalized model (already satisfied — full DTO now populated); UI displays policy before entry incl. min/max age; users can understand rejection reasons (age constraints visible); behavior documented (`docs/Password-Policy.md`).
- [ ] Close: `gh issue close 38 --reason completed --comment "Closed by STAB-021: LdapPasswordChangeProvider now reads pwdProperties (complexity) and pwdHistoryLength from the domain-root domainDNS object (degrading to false/0 on failure, never throwing), AdPasswordPolicyPanel renders minAgeDays/maxAgeDays, and docs/Password-Policy.md documents the display fields, the LDAP domain-root read, and the FGPP-deferred scope. Backend xUnit + frontend Vitest coverage added."`

---

## Final verification (all issues)

- [ ] Full backend suite: `dotnet test src/PassReset.sln --configuration Release`
- [ ] Full frontend suite: `cd src/PassReset.Web/ClientApp && npm test`
- [ ] Installer Pester: `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"`
- [ ] Lint/format frontend: `cd src/PassReset.Web/ClientApp && npm run lint && npm run format:check`
- [ ] Confirm all seven issues show closed: `gh issue list --state closed --search "28 29 30 32 33 36 38"`

**Cross-cutting regression notes:**
- #28's `RecordingSiemService.LogEvent(AuditEvent)` is extended in #30 (Task 30.2) to capture full events — ensure #28 lands first so the recorder exists.
- #29's HttpClient injection changes a constructor signature; the DI container resolves `IHttpClientFactory` automatically (already registered for `"pwned"`), so no other call sites break — but run the full `PasswordControllerTests` + `RateLimitAndRecaptchaTests` after Task 29.2.
- #38's domain-root search adds a second LDAP round-trip per policy fetch; `PasswordPolicyCache` already memoizes the result, so the production cost is one extra search per cache-miss only.
