# Implementation Plan: Health Endpoint Operability Closure (Issue #31 / STAB-018)

## Goal

Drive GitHub issue **#31 (STAB-018 — Healthcheck Enhancement)** to full closure by meeting its two remaining acceptance criteria:

1. **Optional deeper checks are gated by config** — operators can disable individual connectivity probes (SMTP, expiry-service) via configuration, so a probe that cannot run in a restricted network does not force the whole endpoint to `degraded`/`503`.
2. **Install/upgrade scripts can call /health to validate deployment success on a fresh deploy** — when `PasswordExpiryNotificationSettings.Enabled=true`, a not-yet-run expiry service must NOT report `degraded` (which yields `503` and breaks the installer post-deploy check from #34). A not-yet-run service is startup lag, not a failure.

The fix introduces a new `HealthCheckSettings` options class with per-probe disable flags and a configurable grace period for the expiry service, wires it through DI with `ValidateOnStart()`, injects it into `HealthController`, and changes `CheckExpiryService()` so a freshly-started service reports `healthy`. The installer remains unchanged (it already expects `200`); we make the app return `200` on a healthy fresh deploy.

## Architecture

```
appsettings.json
  └─ "HealthCheckSettings": { DisableSmtpConnectivityProbe, DisableExpiryServiceCheck,
                              DisableAdConnectivityProbe, ExpiryServiceGracePeriodSeconds }
        │  (bound via AddOptions<HealthCheckSettings>().Bind().ValidateOnStart())
        ▼
Program.cs  ── registers IOptions<HealthCheckSettings> + HealthCheckSettingsValidator
        ▼
HealthController (GET /api/health)
   CheckAdConnectivityAsync()  ─ gated by DisableAdConnectivityProbe → "skipped"/healthy
   CheckSmtpAsync()            ─ gated by DisableSmtpConnectivityProbe → "skipped"/healthy
   CheckExpiryService()        ─ gated by DisableExpiryServiceCheck → "skipped";
                                 not-yet-run within grace → "healthy" (was "degraded")
        ▼
   aggregate rollup → 200 (healthy) | 503 (degraded/unhealthy)
        ▼
Install-PassReset.ps1 post-deploy probe (#34) — already expects 200, now passes on fresh deploy
```

**Design decision (recorded in the plan, operator-facing):** A service that is *enabled but has not yet completed its first tick* is reported `healthy`, because `LastTickUtc == null` indicates startup, not misconfiguration. After the configurable grace period (default 600s / 10 min) elapses with still-null `LastTickUtc`, it reverts to `degraded` so a genuinely stuck service is still surfaced. This preserves the existing "stuck service → degraded" signal while fixing the fresh-deploy false positive. The probe to compute "within grace period" uses the **process start time** (`Process.GetCurrentProcess().StartTime`), since the expiry service starts at app startup.

## Tech Stack

- **Backend:** C# 13 / ASP.NET Core 10, `Microsoft.Extensions.Options` (`IOptions<T>`, `IValidateOptions<T>`, `ValidateOnStart`).
- **Tests:** xUnit v3 in `src/PassReset.Tests.Windows` (Windows integration tests via `WebApplicationFactory<Program>`). Test command: `dotnet test src/PassReset.sln --configuration Release`.
- **Config surface:** `appsettings.Production.template.json`, `appsettings.schema.json`, docs.
- **Installer:** `deploy/Install-PassReset.ps1` (no code change required; a Pester regression guard confirms the post-deploy contract still expects `200`).

## Agentic-worker sub-skill note

> This plan is written for execution via **`superpowers:subagent-driven-development`** (implementer + spec-review + code-quality loop per task) in the current session. Each task is bite-sized (2–5 min), strictly TDD (failing test first), and ends with an atomic conventional commit. Honor the sequencing notes — `HealthCheckSettings` must exist and be DI-registered before the controller can consume it. This codebase explicitly values **not breaking working code**: Task 1.7 is a dedicated regression-guard task, and every medium/high-risk task carries an explicit "What could break" note.

---

## File Structure

| File | Status | Responsibility |
|------|--------|----------------|
| `src/PassReset.Web/Models/HealthCheckSettings.cs` | **new** | Options POCO: `DisableSmtpConnectivityProbe`, `DisableExpiryServiceCheck`, `DisableAdConnectivityProbe`, `ExpiryServiceGracePeriodSeconds`. One responsibility: hold health-probe toggles. |
| `src/PassReset.Web/Models/HealthCheckSettingsValidator.cs` | **new** | `IValidateOptions<HealthCheckSettings>` — validates `ExpiryServiceGracePeriodSeconds >= 0`. |
| `src/PassReset.Web/Program.cs` | **modified** (after line 97) | Register `AddOptions<HealthCheckSettings>().Bind().ValidateOnStart()` + validator singleton. |
| `src/PassReset.Web/Controllers/HealthController.cs` | **modified** (ctor 32-50; `GetAsync` 56-81; `CheckSmtpAsync` 83-103; `CheckExpiryService` 105-112; `CheckAdConnectivityAsync` 114-124) | Inject `IOptions<HealthCheckSettings>`; gate each probe; treat not-yet-run-within-grace expiry service as healthy. |
| `src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs` | **modified** | Add factories + tests for: fresh-deploy expiry healthy, SMTP probe disabled, expiry check disabled, all probes disabled, grace-period-exceeded → degraded; reuse no-secrets guarantee. |
| `src/PassReset.Web/appsettings.Production.template.json` | **modified** (after line 93) | Add documented `HealthCheckSettings` block with safe defaults. |
| `src/PassReset.Web/appsettings.schema.json` | **modified** (before final `}`) | Add `HealthCheckSettings` schema node (type/properties/default per D-04 keyword set). |
| `docs/appsettings-Production.md` | **modified** | Document the four new keys + fresh-deploy rationale. |
| `deploy/Install-PassReset.Tests.ps1` | **modified** | Pester regression guard: post-deploy probe still asserts `200` (contract unchanged). |

---

## Issue #31 (STAB-018) — Healthcheck Enhancement

> **Dependency note:** `health.json` lists `dependsOn: [19]`. Issue #19 supplied the nested-checks / `IAdConnectivityProbe` / `IExpiryServiceDiagnostics` scaffolding, which is already present in HEAD (`HealthController.cs`, `IExpiryServiceDiagnostics.cs`, `IAdConnectivityProbe.cs`). No blocking work remains from #19 — proceed.
>
> **Execution order:** This is plan order 4 (standalone). It *coordinates* with installer #34 but does not modify the installer's behavior; the installer already expects `200`. We make the app return `200` on a healthy fresh deploy.

---

### Task 1.1 — Create `HealthCheckSettings` options POCO

**Files:** `src/PassReset.Web/Models/HealthCheckSettings.cs` (new)

This task has no behavior yet, so its "test" is a compile-and-bind assertion added in Task 1.2's harness. To keep TDD honest and bite-sized, we write a focused unit test that the type exists with correct defaults.

- [ ] **Step 1 — Write failing test.** Append a new test class file `src/PassReset.Tests.Windows/Web/Models/HealthCheckSettingsTests.cs`:

```csharp
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
}
```

- [ ] **Step 2 — Run it, expect FAIL** (type does not exist → compile error):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthCheckSettingsTests"
```

- [ ] **Step 3 — Minimal implementation.** Create `src/PassReset.Web/Models/HealthCheckSettings.cs`:

```csharp
namespace PassReset.Web.Models;

/// <summary>
/// Operator-managed toggles for the GET /api/health probes. Each connectivity
/// probe can be disabled independently so a host on a restricted network can keep
/// the health endpoint green for the dependencies it CAN reach. Disabling a probe
/// reports its status as "skipped" and excludes it from the aggregate rollup.
/// </summary>
public class HealthCheckSettings
{
    /// <summary>When true, the SMTP TCP connectivity probe is not run (status "skipped").</summary>
    public bool DisableSmtpConnectivityProbe { get; set; }

    /// <summary>When true, the password-expiry background-service check is not run (status "skipped").</summary>
    public bool DisableExpiryServiceCheck { get; set; }

    /// <summary>When true, the Active Directory connectivity probe is not run (status "skipped").</summary>
    public bool DisableAdConnectivityProbe { get; set; }

    /// <summary>
    /// Grace period, in seconds, after process start during which an enabled-but-not-yet-run
    /// expiry service is reported "healthy" rather than "degraded". A not-yet-run service on a
    /// fresh deploy is startup lag, not misconfiguration. After this window with still no tick,
    /// the service reverts to "degraded" so a genuinely stuck service is surfaced. Default 600 (10 min).
    /// </summary>
    public int ExpiryServiceGracePeriodSeconds { get; set; } = 600;
}
```

- [ ] **Step 4 — Run it, expect PASS:**

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthCheckSettingsTests"
```

- [ ] **Step 5 — Commit:**

```bash
git add src/PassReset.Web/Models/HealthCheckSettings.cs src/PassReset.Tests.Windows/Web/Models/HealthCheckSettingsTests.cs
git commit -m "feat(web): add HealthCheckSettings options for per-probe gating [STAB-018]"
```

---

### Task 1.2 — Add `HealthCheckSettingsValidator` (non-negative grace period)

**Files:** `src/PassReset.Web/Models/HealthCheckSettingsValidator.cs` (new)

**Risk: low.** Mirrors the existing `EmailNotificationSettingsValidator` pattern exactly.

- [ ] **Step 1 — Write failing test.** Append to `src/PassReset.Tests.Windows/Web/Models/HealthCheckSettingsTests.cs` (inside the class):

```csharp
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
```

Add `using Microsoft.Extensions.Options;` is not required here (the result type members `Failed/Succeeded/Failures` come from `Microsoft.Extensions.Options.ValidateOptionsResult`, which the validator returns). Add the import to the test file's top:

```csharp
using Microsoft.Extensions.Options;
```

- [ ] **Step 2 — Run it, expect FAIL** (validator type missing → compile error):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthCheckSettingsTests"
```

- [ ] **Step 3 — Minimal implementation.** Create `src/PassReset.Web/Models/HealthCheckSettingsValidator.cs`:

```csharp
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
```

- [ ] **Step 4 — Run it, expect PASS:**

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthCheckSettingsTests"
```

- [ ] **Step 5 — Commit:**

```bash
git add src/PassReset.Web/Models/HealthCheckSettingsValidator.cs src/PassReset.Tests.Windows/Web/Models/HealthCheckSettingsTests.cs
git commit -m "feat(web): validate HealthCheckSettings grace period is non-negative [STAB-018]"
```

---

### Task 1.3 — Register `HealthCheckSettings` in DI with `ValidateOnStart`

**Files:** `src/PassReset.Web/Program.cs` (insert immediately after line 97, the `PasswordChangeOptions` validator registration; before line 99 `AdminSettings`)

**Risk: medium** — touches startup DI. **What could break:** a malformed `HealthCheckSettings` section would now fail `ValidateOnStart` and block startup (502). Mitigation: defaults are valid, the section is optional (binding a missing section yields defaults), and the validator only rejects negative grace periods. Regression guard: the full existing test suite must still pass (Step 4 runs the whole `HealthControllerTests` class to prove startup still binds).

- [ ] **Step 1 — Write failing test.** Add to `src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs` a new test using the existing `DebugFactory` (which already starts the app); it asserts the app resolves `IOptions<HealthCheckSettings>` (proves registration). Add inside `HealthControllerTests`:

```csharp
    // ── Test 7 ─ HealthCheckSettings is registered in DI -------------------------
    [Fact]
    public void HealthCheckSettings_IsRegisteredInDi()
    {
        using var scope = _factory.Services.CreateScope();
        var opts = scope.ServiceProvider
            .GetService<Microsoft.Extensions.Options.IOptions<PassReset.Web.Models.HealthCheckSettings>>();

        Assert.NotNull(opts);
        Assert.NotNull(opts!.Value);
    }
```

Add the required using at the top of the test file:

```csharp
using Microsoft.Extensions.DependencyInjection;
```

- [ ] **Step 2 — Run it, expect FAIL** (no registration → `IOptions<HealthCheckSettings>` resolves but options system returns a default-constructed instance only if `AddOptions` was called; without registration `GetService<IOptions<T>>` returns a generic default — to make this a true RED, assert binding works). Run:

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthCheckSettings_IsRegisteredInDi"
```

> Note: `IOptions<T>` resolves even without explicit `AddOptions` in ASP.NET Core. To guarantee a real RED→GREEN transition, the binding assertion is strengthened in Task 1.4 (which reads a configured value). This task's failing signal is the missing validator wiring caught by Task 1.4's config-driven test. If Step 2 unexpectedly passes, proceed — the registration is still required for `Bind()` + `ValidateOnStart()` and is exercised RED-first by Task 1.5.

- [ ] **Step 3 — Minimal implementation.** In `src/PassReset.Web/Program.cs`, insert after line 97 (`builder.Services.AddSingleton<IValidateOptions<PasswordChangeOptions>, PasswordChangeOptionsValidator>();`):

```csharp
    builder.Services.AddOptions<HealthCheckSettings>()
        .Bind(builder.Configuration.GetSection(nameof(HealthCheckSettings)))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<HealthCheckSettings>, HealthCheckSettingsValidator>();
```

- [ ] **Step 4 — Run it, expect PASS** (and full health suite still green):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthControllerTests"
```

- [ ] **Step 5 — Commit:**

```bash
git add src/PassReset.Web/Program.cs src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs
git commit -m "feat(web): register HealthCheckSettings with ValidateOnStart [STAB-018]"
```

---

### Task 1.4 — Inject `HealthCheckSettings` into `HealthController` and gate the SMTP probe

**Files:** `src/PassReset.Web/Controllers/HealthController.cs` (ctor lines 23-50; `CheckSmtpAsync` lines 83-103)

**Risk: medium.** **What could break:** the constructor signature changes — any direct instantiation in tests would break. Mitigation: all tests construct the controller via `WebApplicationFactory` DI (confirmed in `HealthControllerTests.cs`), so DI supplies the new dependency automatically. The gate is additive (default `false` ⇒ identical existing behavior).

- [ ] **Step 1 — Write failing test.** Add a new factory + test to `HealthControllerTests.cs`. Place the factory after `UnhealthySmtpFactory` and the test inside the class:

```csharp
    // ── Test 8 ─ SMTP probe disabled via config => skipped, aggregate healthy ----
    [Fact]
    public async Task Get_SmtpProbeDisabled_SkipsSmtpAndStaysHealthy()
    {
        using var factory = new SmtpProbeDisabledFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/health");
        var dto = await response.Content.ReadFromJsonAsync<HealthResponseDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(dto);
        Assert.Equal("skipped", dto!.Checks!.Smtp!.Status);
        Assert.True(dto.Checks.Smtp.Skipped);
        Assert.Equal("healthy", dto.Status);
    }
```

And the factory (after `UnhealthySmtpFactory`, before the closing brace of `HealthControllerTests`):

```csharp
    /// <summary>
    /// Email enabled AND SMTP pointed at an unreachable blackhole, BUT the SMTP probe
    /// is disabled via HealthCheckSettings — so the endpoint must stay healthy/200.
    /// </summary>
    public sealed class SmtpProbeDisabledFactory : FakeAdFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var adPort = FakeAdPort.ToString(CultureInfo.InvariantCulture);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebSettings:UseDebugProvider"]                  = "true",
                    ["WebSettings:EnableHttpsRedirect"]               = "false",
                    ["ClientSettings:MinimumDistance"]                = "0",
                    ["ClientSettings:Recaptcha:Enabled"]              = "false",
                    ["EmailNotificationSettings:Enabled"]             = "true",
                    ["PasswordExpiryNotificationSettings:Enabled"]    = "false",
                    ["SiemSettings:Syslog:Enabled"]                   = "false",
                    ["SiemSettings:AlertEmail:Enabled"]               = "false",
                    ["SmtpSettings:Host"]                             = "192.0.2.1",
                    ["SmtpSettings:Port"]                             = "1",
                    ["SmtpSettings:FromAddress"]                      = "passreset@test.invalid",
                    ["HealthCheckSettings:DisableSmtpConnectivityProbe"] = "true",
                    ["PasswordChangeOptions:PortalLockoutThreshold"]  = "0",
                    ["PasswordChangeOptions:UseAutomaticContext"]     = "false",
                    ["PasswordChangeOptions:LdapHostnames:0"]         = "127.0.0.1",
                    ["PasswordChangeOptions:LdapPort"]                = adPort,
                });
            });
        }
    }
```

- [ ] **Step 2 — Run it, expect FAIL** (SMTP probe still runs against the blackhole → `unhealthy` → `503`):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~Get_SmtpProbeDisabled_SkipsSmtpAndStaysHealthy"
```

- [ ] **Step 3 — Minimal implementation.** In `HealthController.cs`:

  (a) Add the field + constructor parameter. Replace lines 30 and the constructor head. Change the field block (after line 30 `private readonly ILogger<HealthController> _logger;`) by inserting before it:

```csharp
    private readonly IOptions<HealthCheckSettings> _healthSettings;
```

  Update the constructor signature — replace `ILogger<HealthController> logger)` (line 40) and the body assignment. Specifically, change the parameter list to add `IOptions<HealthCheckSettings> healthSettings,` immediately before `ILogger<HealthController> logger,` and add the assignment `_healthSettings = healthSettings;` in the body. The resulting constructor:

```csharp
    public HealthController(
        IOptions<PasswordChangeOptions> options,
        IOptions<SmtpSettings> smtp,
        IOptions<EmailNotificationSettings> emailNotif,
        IOptions<PasswordExpiryNotificationSettings> expiryNotif,
        IExpiryServiceDiagnostics expiryDiagnostics,
        ILockoutDiagnostics lockoutDiagnostics,
        IAdConnectivityProbe adProbe,
        IOptions<HealthCheckSettings> healthSettings,
        ILogger<HealthController> logger)
    {
        _options            = options;
        _smtp               = smtp;
        _emailNotif         = emailNotif;
        _expiryNotif        = expiryNotif;
        _expiryDiagnostics  = expiryDiagnostics;
        _lockoutDiagnostics = lockoutDiagnostics;
        _adProbe            = adProbe;
        _healthSettings     = healthSettings;
        _logger             = logger;
    }
```

  (b) Gate the SMTP probe. Replace the body of `CheckSmtpAsync` (lines 83-103) so the disable flag short-circuits to `skipped` before any TCP work:

```csharp
    private async Task<(string status, long latencyMs, bool skipped)> CheckSmtpAsync()
    {
        if (_healthSettings.Value.DisableSmtpConnectivityProbe)
            return ("skipped", 0, true);

        var emailEnabled  = _emailNotif.Value.Enabled;
        var expiryEnabled = _expiryNotif.Value.Enabled;
        if (!emailEnabled && !expiryEnabled)
            return ("skipped", 0, true);

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(_smtp.Value.Host, _smtp.Value.Port, cts.Token);
            return ("healthy", sw.ElapsedMilliseconds, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMTP health check failed ({Host}:{Port})", _smtp.Value.Host, _smtp.Value.Port);
            return ("unhealthy", sw.ElapsedMilliseconds, false);
        }
    }
```

- [ ] **Step 4 — Run it, expect PASS** (plus full suite to confirm no regression in the existing SMTP-skipped/unhealthy tests):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthControllerTests"
```

- [ ] **Step 5 — Commit:**

```bash
git add src/PassReset.Web/Controllers/HealthController.cs src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs
git commit -m "feat(web): gate SMTP health probe behind HealthCheckSettings [STAB-018]"
```

---

### Task 1.5 — Fresh-deploy fix: treat not-yet-run expiry service (within grace) as healthy + gate the check

**Files:** `src/PassReset.Web/Controllers/HealthController.cs` (`CheckExpiryService` lines 105-112)

This is the **core fix** for the installer fresh-deploy `503` problem.

**Risk: medium-high.** **What could break:** the previous behavior reported `degraded` whenever `LastTickUtc == null`. Operators/monitors may rely on `degraded` to detect a *stuck* service. Mitigation: we preserve that signal — after `ExpiryServiceGracePeriodSeconds` from process start, a still-null tick reverts to `degraded`. The regression guard in Task 1.6 asserts the grace-exceeded path still yields `degraded`/`503`. We compute elapsed-since-start from `Process.GetCurrentProcess().StartTime.ToUniversalTime()` (the process hosting the app and the background service share a start time).

- [ ] **Step 1 — Write failing tests.** Add a factory that enables the expiry service (so `IsEnabled=true`, `LastTickUtc=null` on a fresh start) and a test asserting `healthy`/`200`. Add the test inside `HealthControllerTests`:

```csharp
    // ── Test 9 ─ Fresh deploy: expiry enabled, not yet run => healthy/200 --------
    [Fact]
    public async Task Get_ExpiryService_EnabledButNotYetRun_ReturnsHealthy()
    {
        using var factory = new ExpiryEnabledFreshFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/health");
        var dto = await response.Content.ReadFromJsonAsync<HealthResponseDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(dto);
        Assert.Equal("healthy", dto!.Checks!.ExpiryService!.Status);
        Assert.Equal("healthy", dto.Status);
    }

    // ── Test 10 ─ Expiry check disabled via config => skipped --------------------
    [Fact]
    public async Task Get_ExpiryCheckDisabled_SkipsExpiryService()
    {
        using var factory = new ExpiryCheckDisabledFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/health");
        var dto = await response.Content.ReadFromJsonAsync<HealthResponseDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(dto);
        Assert.Equal("skipped", dto!.Checks!.ExpiryService!.Status);
        Assert.Equal("healthy", dto.Status);
    }
```

Add both factories (after `SmtpProbeDisabledFactory`):

```csharp
    /// <summary>
    /// Expiry notification enabled with a real background service that has not yet
    /// ticked (LastTickUtc=null). With the default grace period this must report
    /// "healthy" so a fresh deploy returns 200 — the #34 installer contract.
    /// </summary>
    public sealed class ExpiryEnabledFreshFactory : FakeAdFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var adPort = FakeAdPort.ToString(CultureInfo.InvariantCulture);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebSettings:UseDebugProvider"]                  = "true",
                    ["WebSettings:EnableHttpsRedirect"]               = "false",
                    ["ClientSettings:MinimumDistance"]                = "0",
                    ["ClientSettings:Recaptcha:Enabled"]              = "false",
                    ["EmailNotificationSettings:Enabled"]             = "false",
                    // Enable the expiry service so the diagnostics report IsEnabled=true.
                    ["PasswordExpiryNotificationSettings:Enabled"]    = "true",
                    ["PasswordExpiryNotificationSettings:NotificationTimeUtc"] = "08:00",
                    ["PasswordExpiryNotificationSettings:PassResetUrl"]        = "https://passreset.test.invalid",
                    ["PasswordExpiryNotificationSettings:DaysBeforeExpiry"]    = "14",
                    ["SiemSettings:Syslog:Enabled"]                   = "false",
                    ["SiemSettings:AlertEmail:Enabled"]               = "false",
                    // SMTP probe disabled so the (now-enabled) email path can't make the
                    // test flaky — this test isolates the expiry-service behavior.
                    ["HealthCheckSettings:DisableSmtpConnectivityProbe"] = "true",
                    ["PasswordChangeOptions:PortalLockoutThreshold"]  = "0",
                    ["PasswordChangeOptions:UseAutomaticContext"]     = "false",
                    ["PasswordChangeOptions:LdapHostnames:0"]         = "127.0.0.1",
                    ["PasswordChangeOptions:LdapPort"]                = adPort,
                });
            });
        }
    }

    /// <summary>
    /// Expiry enabled and not-yet-run, but the expiry CHECK is disabled via
    /// HealthCheckSettings — must report "skipped".
    /// </summary>
    public sealed class ExpiryCheckDisabledFactory : FakeAdFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var adPort = FakeAdPort.ToString(CultureInfo.InvariantCulture);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebSettings:UseDebugProvider"]                  = "true",
                    ["WebSettings:EnableHttpsRedirect"]               = "false",
                    ["ClientSettings:MinimumDistance"]                = "0",
                    ["ClientSettings:Recaptcha:Enabled"]              = "false",
                    ["EmailNotificationSettings:Enabled"]             = "false",
                    ["PasswordExpiryNotificationSettings:Enabled"]    = "true",
                    ["PasswordExpiryNotificationSettings:NotificationTimeUtc"] = "08:00",
                    ["PasswordExpiryNotificationSettings:PassResetUrl"]        = "https://passreset.test.invalid",
                    ["PasswordExpiryNotificationSettings:DaysBeforeExpiry"]    = "14",
                    ["SiemSettings:Syslog:Enabled"]                   = "false",
                    ["SiemSettings:AlertEmail:Enabled"]               = "false",
                    ["HealthCheckSettings:DisableSmtpConnectivityProbe"] = "true",
                    ["HealthCheckSettings:DisableExpiryServiceCheck"]    = "true",
                    ["PasswordChangeOptions:PortalLockoutThreshold"]  = "0",
                    ["PasswordChangeOptions:UseAutomaticContext"]     = "false",
                    ["PasswordChangeOptions:LdapHostnames:0"]         = "127.0.0.1",
                    ["PasswordChangeOptions:LdapPort"]                = adPort,
                });
            });
        }
    }
```

> **Sequencing note:** `ExpiryEnabledFreshFactory` registers the real expiry background service. Confirm `Program.cs` lines 325/354 register `IExpiryServiceDiagnostics` from the running service when `PasswordExpiryNotificationSettings.Enabled=true` (verified during research). The background service will not have ticked by the time the first `/api/health` request runs, so `LastTickUtc` is reliably `null` within the test window.

- [ ] **Step 2 — Run it, expect FAIL** (current `CheckExpiryService` returns `degraded` when `LastTickUtc==null` → aggregate `degraded` → `503`; and the disable flag is not yet honored):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~Get_ExpiryService_EnabledButNotYetRun_ReturnsHealthy|FullyQualifiedName~Get_ExpiryCheckDisabled_SkipsExpiryService"
```

- [ ] **Step 3 — Minimal implementation.** Replace `CheckExpiryService` (lines 105-112) in `HealthController.cs`:

```csharp
    private (string status, long latencyMs) CheckExpiryService()
    {
        if (_healthSettings.Value.DisableExpiryServiceCheck)
            return ("skipped", 0);

        if (!_expiryDiagnostics.IsEnabled)
            return ("not-enabled", 0);

        if (_expiryDiagnostics.LastTickUtc is null)
        {
            // A service enabled but not-yet-run on its first tick is not unhealthy; the
            // initial null indicates startup lag, not misconfiguration. Within the grace
            // window (measured from process start) report "healthy" so a fresh deploy
            // returns 200 for the installer post-deploy check. Past the window with still
            // no tick, surface "degraded" so a genuinely stuck service is visible.
            var grace = TimeSpan.FromSeconds(_healthSettings.Value.ExpiryServiceGracePeriodSeconds);
            var startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(startedUtc, TimeSpan.Zero);
            return elapsed <= grace ? ("healthy", 0) : ("degraded", 0);
        }

        return ("healthy", 0);
    }
```

> `Process` is already imported (`using System.Diagnostics;` at line 1, used by `Stopwatch`).

- [ ] **Step 4 — Run it, expect PASS** (plus full suite — Test 3 `Get_ExpiryService_NotEnabled_ReturnsHealthy` must still pass since the `not-enabled` path is unchanged):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthControllerTests"
```

- [ ] **Step 5 — Commit:**

```bash
git add src/PassReset.Web/Controllers/HealthController.cs src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs
git commit -m "fix(web): not-yet-run expiry service is healthy within grace so fresh deploy returns 200 [STAB-018]"
```

---

### Task 1.6 — Gate the AD probe + "all probes disabled" + grace-exceeded regression guard

**Files:** `src/PassReset.Web/Controllers/HealthController.cs` (`CheckAdConnectivityAsync` lines 114-124)

**Risk: medium-high (regression-guard task).** This task adds the AD gate AND the explicit regression test that a *stuck* service (grace=0, not-yet-run) still reports `degraded`/`503` — proving Task 1.5 did not erase the stuck-service signal. **What could break:** disabling the AD probe in a real domain-joined deploy hides genuine AD outages; this is operator opt-in (default `false`), documented in Task 1.9.

- [ ] **Step 1 — Write failing tests.** Add to `HealthControllerTests`:

```csharp
    // ── Test 11 ─ Grace exceeded (grace=0) + not-yet-run => degraded/503 ---------
    //  Regression guard: a genuinely stuck expiry service is still surfaced.
    [Fact]
    public async Task Get_ExpiryService_NotYetRun_GraceZero_ReportsDegraded()
    {
        using var factory = new ExpiryStuckFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/health");
        var dto = await response.Content.ReadFromJsonAsync<HealthResponseDto>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(dto);
        Assert.Equal("degraded", dto!.Checks!.ExpiryService!.Status);
        Assert.Equal("degraded", dto.Status);
    }

    // ── Test 12 ─ All probes disabled => 200 even with all deps unreachable ------
    [Fact]
    public async Task Get_AllProbesDisabled_Returns200_EvenWhenDepsUnreachable()
    {
        using var factory = new AllProbesDisabledFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/health");
        var dto = await response.Content.ReadFromJsonAsync<HealthResponseDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(dto);
        Assert.Equal("healthy", dto!.Status);
        Assert.Equal("skipped", dto.Checks!.Ad!.Status);
        Assert.Equal("skipped", dto.Checks.Smtp!.Status);
        Assert.Equal("skipped", dto.Checks.ExpiryService!.Status);
    }
```

Add the factories (after `ExpiryCheckDisabledFactory`). `ExpiryStuckFactory` sets grace to 0 so a not-yet-run service is immediately "stuck"; it does NOT bind to the fake AD listener (uses an unreachable LDAP host would make AD unhealthy, so instead keep AD healthy via the listener to isolate the expiry signal):

```csharp
    /// <summary>
    /// Expiry enabled, not-yet-run, grace period = 0 → must report "degraded" (stuck).
    /// Regression guard that Task 1.5 did not erase the stuck-service signal. AD stays
    /// healthy (loopback listener) so the only non-healthy check is the expiry service.
    /// </summary>
    public sealed class ExpiryStuckFactory : FakeAdFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var adPort = FakeAdPort.ToString(CultureInfo.InvariantCulture);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebSettings:UseDebugProvider"]                  = "true",
                    ["WebSettings:EnableHttpsRedirect"]               = "false",
                    ["ClientSettings:MinimumDistance"]                = "0",
                    ["ClientSettings:Recaptcha:Enabled"]              = "false",
                    ["EmailNotificationSettings:Enabled"]             = "false",
                    ["PasswordExpiryNotificationSettings:Enabled"]    = "true",
                    ["PasswordExpiryNotificationSettings:NotificationTimeUtc"] = "08:00",
                    ["PasswordExpiryNotificationSettings:PassResetUrl"]        = "https://passreset.test.invalid",
                    ["PasswordExpiryNotificationSettings:DaysBeforeExpiry"]    = "14",
                    ["SiemSettings:Syslog:Enabled"]                   = "false",
                    ["SiemSettings:AlertEmail:Enabled"]               = "false",
                    ["HealthCheckSettings:DisableSmtpConnectivityProbe"]   = "true",
                    ["HealthCheckSettings:ExpiryServiceGracePeriodSeconds"] = "0",
                    ["PasswordChangeOptions:PortalLockoutThreshold"]  = "0",
                    ["PasswordChangeOptions:UseAutomaticContext"]     = "false",
                    ["PasswordChangeOptions:LdapHostnames:0"]         = "127.0.0.1",
                    ["PasswordChangeOptions:LdapPort"]                = adPort,
                });
            });
        }
    }

    /// <summary>
    /// Every probe disabled via HealthCheckSettings while every dependency is unreachable
    /// (blackhole SMTP, no AD listener bound, expiry stuck). Endpoint must still be 200/healthy.
    /// </summary>
    public sealed class AllProbesDisabledFactory : FakeAdFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WebSettings:UseDebugProvider"]                  = "true",
                    ["WebSettings:EnableHttpsRedirect"]               = "false",
                    ["ClientSettings:MinimumDistance"]                = "0",
                    ["ClientSettings:Recaptcha:Enabled"]              = "false",
                    ["EmailNotificationSettings:Enabled"]             = "true",
                    ["PasswordExpiryNotificationSettings:Enabled"]    = "true",
                    ["PasswordExpiryNotificationSettings:NotificationTimeUtc"] = "08:00",
                    ["PasswordExpiryNotificationSettings:PassResetUrl"]        = "https://passreset.test.invalid",
                    ["PasswordExpiryNotificationSettings:DaysBeforeExpiry"]    = "14",
                    ["SiemSettings:Syslog:Enabled"]                   = "false",
                    ["SiemSettings:AlertEmail:Enabled"]               = "false",
                    ["SmtpSettings:Host"]                             = "192.0.2.1",
                    ["SmtpSettings:Port"]                             = "1",
                    ["SmtpSettings:FromAddress"]                      = "passreset@test.invalid",
                    ["HealthCheckSettings:DisableSmtpConnectivityProbe"]    = "true",
                    ["HealthCheckSettings:DisableExpiryServiceCheck"]       = "true",
                    ["HealthCheckSettings:DisableAdConnectivityProbe"]      = "true",
                    ["PasswordChangeOptions:PortalLockoutThreshold"]  = "0",
                    ["PasswordChangeOptions:UseAutomaticContext"]     = "false",
                    // Point AD at an unreachable host to prove the disabled probe never runs.
                    ["PasswordChangeOptions:LdapHostnames:0"]         = "192.0.2.1",
                    ["PasswordChangeOptions:LdapPort"]                = "1",
                });
            });
        }
    }
```

- [ ] **Step 2 — Run it, expect FAIL** (`AllProbesDisabled` fails because the AD gate doesn't exist yet → AD probes the unreachable host → `unhealthy`/`503`; `ExpiryStuck` should already pass given Task 1.5, but is included here as the regression guard):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~Get_AllProbesDisabled_Returns200_EvenWhenDepsUnreachable|FullyQualifiedName~Get_ExpiryService_NotYetRun_GraceZero_ReportsDegraded"
```

- [ ] **Step 3 — Minimal implementation.** Gate the AD probe — replace `CheckAdConnectivityAsync` (lines 114-124) in `HealthController.cs`. Because the aggregate uses `adResult.status` directly and the wire DTO has no `skipped` field for AD, return `"healthy"` when disabled (so aggregate stays healthy) but log the skip; to match the test's `Assert.Equal("skipped", dto.Checks.Ad.Status)`, return the literal `"skipped"` status (which is neither `unhealthy` nor `degraded`, so the aggregate rollup treats it as healthy):

```csharp
    private async Task<(string status, long latencyMs)> CheckAdConnectivityAsync()
    {
        if (_healthSettings.Value.DisableAdConnectivityProbe)
            return ("skipped", 0);

        var result = await _adProbe.CheckAsync(HttpContext.RequestAborted);
        var status = result.Status switch
        {
            AdProbeStatus.Healthy        => "healthy",
            AdProbeStatus.NotConfigured  => "healthy",   // debug/unconfigured scenarios
            _                            => "unhealthy",
        };
        return (status, result.LatencyMs);
    }
```

> The aggregate at lines 63-65 only escalates on `"unhealthy"` or `"degraded"`; `"skipped"` falls through to `"healthy"` — exactly the desired rollup. No change needed to `GetAsync`.

- [ ] **Step 4 — Run it, expect PASS** (full health suite — including the original Tests 1–6 to confirm no regression):

```bash
dotnet test src/PassReset.sln --configuration Release --filter "FullyQualifiedName~HealthControllerTests"
```

- [ ] **Step 5 — Commit:**

```bash
git add src/PassReset.Web/Controllers/HealthController.cs src/PassReset.Tests.Windows/Web/Controllers/HealthControllerTests.cs
git commit -m "feat(web): gate AD probe + guard stuck-expiry-service degraded signal [STAB-018]"
```

---

### Task 1.7 — Installer post-deploy contract regression guard (Pester)

**Files:** `deploy/Install-PassReset.Tests.ps1` (add a focused `Describe`/`It`)

**Risk: low** — test-only. Confirms the installer post-deploy logic (`Install-PassReset.ps1` lines 1483, 1491-1495) still treats `200` as success and `503`/non-200 as fatal, so the app-side fix (return `200` on healthy fresh deploy) closes the loop with #34 without a script change. This is a static-text contract assertion against the installer script (no IIS required), keeping it CI-runnable on `windows-latest`.

- [ ] **Step 1 — Write failing test.** Append to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'STAB-018 / #34 post-deploy health contract' {
    BeforeAll {
        $script:InstallerPath = Join-Path $PSScriptRoot 'Install-PassReset.ps1'
        $script:InstallerText = Get-Content -Raw -Path $script:InstallerPath
    }

    It 'treats HTTP 200 from /api/health as deployment success' {
        $script:InstallerText | Should -Match '\$lastHealth\.StatusCode -eq 200'
    }

    It 'queries the /api/health endpoint during post-deploy verification' {
        $script:InstallerText | Should -Match '/api/health'
    }

    It 'hard-fails the install when the health check never returns 200' {
        # The verification block must exit non-zero on final failure.
        $script:InstallerText | Should -Match 'Post-deploy health check failed'
        $script:InstallerText | Should -Match 'exit 1'
    }
}
```

- [ ] **Step 2 — Run it, expect PASS-after-RED check.** First confirm it executes (this guard documents existing behavior, so it should pass immediately; to honor TDD, verify the assertions are meaningful by temporarily checking against a wrong string mentally — then run for real):

```powershell
Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*post-deploy health contract*'
```

> If any `It` fails, the installer contract drifted — STOP and reconcile against `Install-PassReset.ps1` lines 1481-1495 before proceeding.

- [ ] **Step 3 — Minimal implementation.** None required (guard documents existing installer behavior). If an assertion fails because the installer wording differs, adjust the regex to the actual text at `Install-PassReset.ps1:1483/1493/1494` — do NOT weaken the `200` assertion.

- [ ] **Step 4 — Run it, expect PASS:**

```powershell
Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*post-deploy health contract*'
```

- [ ] **Step 5 — Commit:**

```bash
git add deploy/Install-PassReset.Tests.ps1
git commit -m "test(installer): guard post-deploy /api/health 200 contract [STAB-018]"
```

---

### Task 1.8 — Add `HealthCheckSettings` to the config template and JSON schema

**Files:** `src/PassReset.Web/appsettings.Production.template.json` (insert after the `PasswordExpiryNotificationSettings` block, line 93); `src/PassReset.Web/appsettings.schema.json` (insert a `HealthCheckSettings` property node before the final closing braces)

**Risk: low-medium.** **What could break:** the installer's `-ConfigSync` additive-merge (D-11) reads the schema; an invalid schema node would break `Test-Json`. Mitigation: restrict to the D-04 keyword set (`type`/`properties`/`default`/`minimum`/`additionalProperties`) and validate with `Test-Json` in Step 4.

- [ ] **Step 1 — Write failing test.** Add a Pester assertion to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'STAB-018 HealthCheckSettings config surface' {
    BeforeAll {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $script:Template = Join-Path $repoRoot 'src/PassReset.Web/appsettings.Production.template.json'
        $script:Schema   = Join-Path $repoRoot 'src/PassReset.Web/appsettings.schema.json'
    }

    It 'template includes a HealthCheckSettings block with all four keys' {
        $json = Get-Content -Raw -Path $script:Template | ConvertFrom-Json
        $json.HealthCheckSettings | Should -Not -BeNullOrEmpty
        $json.HealthCheckSettings.DisableSmtpConnectivityProbe    | Should -BeOfType [bool]
        $json.HealthCheckSettings.DisableExpiryServiceCheck       | Should -BeOfType [bool]
        $json.HealthCheckSettings.DisableAdConnectivityProbe      | Should -BeOfType [bool]
        $json.HealthCheckSettings.ExpiryServiceGracePeriodSeconds | Should -Be 600
    }

    It 'schema declares HealthCheckSettings and remains valid JSON' {
        $schemaText = Get-Content -Raw -Path $script:Schema
        $schemaText | Should -Match 'HealthCheckSettings'
        { $schemaText | ConvertFrom-Json } | Should -Not -Throw
    }
}
```

- [ ] **Step 2 — Run it, expect FAIL** (template + schema lack the block):

```powershell
Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*HealthCheckSettings config surface*'
```

- [ ] **Step 3 — Minimal implementation.**

  (a) In `appsettings.Production.template.json`, after the `PasswordExpiryNotificationSettings` block (the `}` on line 93) add a comma and the new block (insert before the `SiemSettings` block on line 95):

```json
  "HealthCheckSettings": {
    "DisableSmtpConnectivityProbe": false,
    "DisableExpiryServiceCheck": false,
    "DisableAdConnectivityProbe": false,
    "ExpiryServiceGracePeriodSeconds": 600
  },
```

  (b) In `appsettings.schema.json`, add a property node inside the top-level `"properties"` object (e.g., after the `Serilog` node at lines 16-19, before `WebSettings`):

```json
    "HealthCheckSettings": {
      "type": "object",
      "additionalProperties": true,
      "properties": {
        "DisableSmtpConnectivityProbe":    { "type": "boolean", "default": false },
        "DisableExpiryServiceCheck":       { "type": "boolean", "default": false },
        "DisableAdConnectivityProbe":      { "type": "boolean", "default": false },
        "ExpiryServiceGracePeriodSeconds": { "type": "integer", "minimum": 0, "default": 600 }
      }
    },
```

- [ ] **Step 4 — Run it, expect PASS** (and confirm the template + schema parse as JSON):

```powershell
Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*HealthCheckSettings config surface*'
Get-Content -Raw src/PassReset.Web/appsettings.schema.json | Test-Json
Get-Content -Raw src/PassReset.Web/appsettings.Production.template.json | Test-Json
```

- [ ] **Step 5 — Commit:**

```bash
git add src/PassReset.Web/appsettings.Production.template.json src/PassReset.Web/appsettings.schema.json deploy/Install-PassReset.Tests.ps1
git commit -m "feat(deploy): add HealthCheckSettings to config template and schema [STAB-018]"
```

---

### Task 1.9 — Document the new keys + fresh-deploy rationale

**Files:** `docs/appsettings-Production.md` (add a `HealthCheckSettings` subsection)

**Risk: low** — docs only. Use the Technical Writer voice per the project commit convention.

- [ ] **Step 1 — Write failing test (doc-coverage guard).** Add to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'STAB-018 documentation' {
    It 'appsettings-Production.md documents HealthCheckSettings and the four keys' {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $doc = Get-Content -Raw -Path (Join-Path $repoRoot 'docs/appsettings-Production.md')
        $doc | Should -Match 'HealthCheckSettings'
        $doc | Should -Match 'DisableSmtpConnectivityProbe'
        $doc | Should -Match 'DisableExpiryServiceCheck'
        $doc | Should -Match 'DisableAdConnectivityProbe'
        $doc | Should -Match 'ExpiryServiceGracePeriodSeconds'
    }
}
```

- [ ] **Step 2 — Run it, expect FAIL:**

```powershell
Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*STAB-018 documentation*'
```

- [ ] **Step 3 — Minimal implementation.** Add this subsection to `docs/appsettings-Production.md` (under the health/monitoring area; if no such heading exists, append a new top-level section near the SMTP/expiry docs):

```markdown
## HealthCheckSettings

Controls the per-dependency probes run by `GET /api/health`. All probes are enabled
by default. Disabling a probe reports its status as `skipped` and excludes it from the
aggregate rollup, so a host on a restricted network can keep the endpoint green for the
dependencies it can actually reach.

| Key | Type | Default | Effect |
|-----|------|---------|--------|
| `DisableSmtpConnectivityProbe` | bool | `false` | Skip the SMTP TCP probe. Use on hosts where the relay is firewalled off from the web tier but mail still flows from elsewhere. |
| `DisableExpiryServiceCheck` | bool | `false` | Skip the password-expiry background-service check entirely. |
| `DisableAdConnectivityProbe` | bool | `false` | Skip the Active Directory reachability probe. Use with care — this hides genuine AD outages from monitoring. |
| `ExpiryServiceGracePeriodSeconds` | int (>= 0) | `600` | Window after process start during which an enabled-but-not-yet-run expiry service reports `healthy` instead of `degraded`. |

### Fresh-deploy behavior (why `ExpiryServiceGracePeriodSeconds` exists)

When `PasswordExpiryNotificationSettings.Enabled` is `true`, the background service runs
on a daily schedule and may not have ticked when the installer's post-deploy check calls
`/api/health` seconds after deployment. A not-yet-run service is **startup lag, not
misconfiguration**, so within the grace window the expiry check reports `healthy` and the
endpoint returns `200` — allowing `Install-PassReset.ps1` to confirm a successful deploy.
After the grace window with still no tick, the check reverts to `degraded` so a genuinely
stuck service is surfaced to monitoring. Set the window to `0` to treat any not-yet-run
service as immediately `degraded` (legacy behavior).
```

- [ ] **Step 4 — Run it, expect PASS:**

```powershell
Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*STAB-018 documentation*'
```

- [ ] **Step 5 — Commit:**

```bash
git add docs/appsettings-Production.md deploy/Install-PassReset.Tests.ps1
git commit -m "docs(docs): document HealthCheckSettings probe toggles and fresh-deploy grace [STAB-018]"
```

---

### Task 1.10 — Full-suite verification + acceptance-criteria sign-off + close #31

**Files:** none (verification + GitHub)

This task proves **every** #31 acceptance criterion is met and closes the issue.

- [ ] **Step 1 — Run the complete backend suite (no filter) to prove zero regressions:**

```bash
dotnet build src/PassReset.sln --configuration Release
dotnet test src/PassReset.sln --configuration Release
```

- [ ] **Step 2 — Run the full installer Pester suite:**

```powershell
Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed
```

- [ ] **Step 3 — Map results to acceptance criteria (all must be ✅):**

| #31 Acceptance criterion | Proven by |
|--------------------------|-----------|
| Optional deeper checks are gated by config (operator can disable individual probes) | Tests 8 (`Get_SmtpProbeDisabled_…`), 10 (`Get_ExpiryCheckDisabled_…`), 12 (`Get_AllProbesDisabled_…`) + `HealthCheckSettings` keys in template/schema/docs |
| Install/upgrade scripts can call /health to validate deployment success on a **fresh deploy** | Test 9 (`Get_ExpiryService_EnabledButNotYetRun_ReturnsHealthy` → 200) + Task 1.7 installer-contract guard |
| Stuck/misconfigured service still surfaced (no regression of existing signal) | Test 11 (`Get_ExpiryService_NotYetRun_GraceZero_ReportsDegraded` → 503) |
| No secrets leak in body (existing guarantee preserved) | Test 4 (`Health_Body_ContainsNoSecrets`) still green |
| Existing wire shape / aggregate rollup unchanged | Tests 1, 2, 3, 5, 6 still green |
| Config validated at startup | `HealthCheckSettingsValidator` tests + `ValidateOnStart` registration |

- [ ] **Step 4 — Confirm there are no open CodeQL alerts introduced by the change** (quick sanity, optional if offline):

```bash
gh api repos/:owner/:repo/code-scanning/alerts --jq '.[] | select(.state=="open") | {number, rule: .rule.id, path: .most_recent_instance.location.path}' 2>/dev/null | head
```

- [ ] **Step 5 — Close the issue on GitHub with a summary comment:**

```bash
gh issue close 31 --reason completed --comment "STAB-018 closed. Added HealthCheckSettings with independent probe toggles (DisableSmtpConnectivityProbe / DisableExpiryServiceCheck / DisableAdConnectivityProbe) and ExpiryServiceGracePeriodSeconds. A not-yet-run expiry service now reports healthy within the grace window, so a fresh deploy returns 200 and the Install-PassReset.ps1 post-deploy check (#34) passes; a stuck service past the grace window still reports degraded/503. Covered by new xUnit tests in HealthControllerTests (fresh-deploy healthy, SMTP/expiry/AD probe disabled, all-probes-disabled, grace-exceeded degraded) plus a Pester guard on the installer 200-contract and config template/schema/docs. Full backend + installer suites green."
```

---

## Sequencing summary

1. **1.1 → 1.2** create the options type + validator (no consumers yet).
2. **1.3** registers them in DI (`ValidateOnStart`) — required before the controller can inject.
3. **1.4 → 1.5 → 1.6** consume the settings in `HealthController`: SMTP gate, the core fresh-deploy expiry fix, then AD gate + stuck-service regression guard.
4. **1.7** locks the installer↔app contract (coordinates with #34).
5. **1.8 → 1.9** surface the config in template/schema/docs.
6. **1.10** verifies all criteria + closes #31.

## Regression risk register

| Change | Risk | Guard |
|--------|------|-------|
| Constructor signature change (1.4) | Direct controller instantiation breaks | All tests use `WebApplicationFactory` DI — confirmed; full suite in 1.4/1.10 |
| Not-yet-run expiry → healthy (1.5) | Hides a stuck service | Grace window reverts to `degraded`; Test 11 (grace=0) proves the signal survives |
| Disabling AD probe (1.6) | Hides real AD outages | Operator opt-in (default `false`); documented in 1.9 |
| Startup `ValidateOnStart` for new options (1.3) | Bad config blocks startup | Defaults valid; only negative grace rejected; full suite proves startup binds |
| Schema/template edits (1.8) | Breaks installer `-ConfigSync` `Test-Json` | `Test-Json` validation in 1.8 Step 4; D-04 keyword set only |

**Net behavioral change for existing deployments:** with no `HealthCheckSettings` section present, all flags default `false` and grace defaults `600s`. The *only* behavior difference from HEAD is that an enabled-but-not-yet-run expiry service reports `healthy` (was `degraded`) for the first 10 minutes after process start — which is precisely the fix #31 requires and is benign for monitoring.
