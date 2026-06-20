# Deepen the Password Change Flow — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the inline Password Change orchestration out of `PasswordController.PostAsync` into a deep, cross-platform `ChangePasswordFlow` module behind one interface, folding STAB-013 redaction into its own `IErrorRedactor` seam — so the flow is testable without HTTP and the controller becomes a thin adapter.

**Architecture:** A new `ChangePasswordFlow` module lives in `PassReset.Common` (cross-platform, `net10.0`) and owns the full sequence above the [[Password Changer]] seam: minimum-distance check → reCAPTCHA gate → credentialed change → [[Error Redaction]] → SIEM audit at each decision point. It returns a `ChangePasswordOutcome` result type (disposition + already-redacted error + success message + a notification *intent*). The controller maps `ModelState`-validated input into a `ChangePasswordRequest`, calls the flow, switches on the outcome to produce `Ok`/`BadRequest(ApiResult)`, and fires the email notification `Task.Run` exactly as today (off the response path). Three interfaces/DTOs relocate from `PassReset.Web` to `PassReset.Common` — `IRecaptchaVerifier`, `ISiemService` (+ `SiemEventType`, `AuditEvent`) — so the flow can depend on them; their *implementations* stay in `PassReset.Web`.

**Tech Stack:** C# 13, .NET 10 (`net10.0` for Common, `net10.0-windows` for Web), xUnit v3, NSubstitute 5.3.0 (already referenced by `PassReset.Tests`), ASP.NET Core 10.

## Global Constraints

- **Behavior preservation is the prime directive.** The 11 existing integration tests in `src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs` and the tests in `StatusEndpointTests.cs` test the change/status flow end-to-end through `WebApplicationFactory<Program>`. They MUST pass unchanged after every task. They are the safety net — do not edit them except where a task explicitly says so.
- **Common stays platform-neutral.** `PassReset.Common` targets `net10.0` and must NOT reference ASP.NET Core hosting types (`IHostEnvironment`, `ModelStateDictionary`, MVC) or `System.Net.Sockets`. Only interfaces and POCO/record DTOs relocate into it — never implementations that carry Web/socket dependencies.
- **No latency regression.** The success-path email notification stays fire-and-forget via `Task.Run` in the controller, off the HTTP response path. The flow must NOT call `IDirectoryUserReader.GetUserEmail` (a synchronous LDAP round-trip) or `IEmailService.SendAsync`.
- **`ModelState` validation stays in the controller.** It is framework-bound (`ModelStateDictionary`). The flow owns only the non-ModelState validations: the minimum-distance check.
- **Redaction semantics are exact (STAB-013 / D-05):** In Production, `InvalidCredentials` and `UserNotFound` collapse to `Generic` on the wire; `ApproachingLockout` and `PortalLockout` do NOT. SIEM granularity is preserved independently of the wire response. Outside Production, nothing is redacted.
- **Commit convention:** `type(scope): subject` enforced by `.githooks/commit-msg`. Types: `feat fix refactor docs chore test ci perf style`. Scopes: `web provider common docs ci deps security installer`. Use `refactor(common)`, `refactor(web)`, `test(common)`, `test(web)` as appropriate.
- **Build/test commands:**
  - Build all: `dotnet build src/PassReset.sln -c Release`
  - Cross-platform tests (Linux CI leg): `dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release`
  - Windows tests (the safety net): `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj -c Release`

---

## File Structure

**New files (in `PassReset.Common`, the deep module + its seam):**
- `src/PassReset.Common/ChangeFlow/ChangePasswordRequest.cs` — input record (username, currentPassword, newPassword) + `RequestContext` (traceId, clientIp).
- `src/PassReset.Common/ChangeFlow/ChangePasswordOutcome.cs` — output record (`Disposition`, `ApiErrorItem?`, success message, `NotificationRequest?`).
- `src/PassReset.Common/ChangeFlow/IChangePasswordFlow.cs` — the module interface.
- `src/PassReset.Common/ChangeFlow/ChangePasswordFlow.cs` — the implementation (owns the sequence).
- `src/PassReset.Common/ChangeFlow/IErrorRedactor.cs` — the [[Error Redaction]] seam.
- `src/PassReset.Common/ChangeFlow/IChangeFlowSettings.cs` — minimal settings the flow reads (`MinimumDistance`, recaptcha enabled+action+private-key-present, notification enabled).

**Relocated files (move `PassReset.Web` → `PassReset.Common`, interfaces/DTOs only):**
- `IRecaptchaVerifier` → `src/PassReset.Common/IRecaptchaVerifier.cs`
- `ISiemService` + `SiemEventType` → `src/PassReset.Common/ISiemService.cs`
- `AuditEvent` → `src/PassReset.Common/AuditEvent.cs`

**Modified files:**
- `src/PassReset.Web/Services/SiemService.cs` — add `using PassReset.Common;` (interface moved).
- `src/PassReset.Web/Services/GoogleRecaptchaVerifier.cs` — add `using PassReset.Common;`.
- `src/PassReset.Web/Services/IErrorRedactor` adapter — new `src/PassReset.Web/Services/HostEnvironmentErrorRedactor.cs` reading `IHostEnvironment`.
- `src/PassReset.Web/Controllers/PasswordController.cs` — `PostAsync` becomes thin; redaction/SIEM-mapping helpers move out.
- `src/PassReset.Web/Program.cs` — register `IChangePasswordFlow`, `IErrorRedactor`, and the flow settings adapter.

**New test files:**
- `src/PassReset.Tests/ChangeFlow/ErrorRedactorTests.cs` — pure redaction unit tests (cross-platform).
- `src/PassReset.Tests/ChangeFlow/ChangePasswordFlowTests.cs` — the flow's behavior, tested without HTTP (cross-platform).

---

### Task 1: Relocate `IRecaptchaVerifier` to `PassReset.Common`

**Files:**
- Create: `src/PassReset.Common/IRecaptchaVerifier.cs`
- Modify: `src/PassReset.Web/Services/IRecaptchaVerifier.cs` (delete)
- Modify: `src/PassReset.Web/Services/GoogleRecaptchaVerifier.cs` (add using)

**Interfaces:**
- Produces: `PassReset.Common.IRecaptchaVerifier` with `Task<bool> VerifyAsync(string token, string action, string clientIp)`.

- [ ] **Step 1: Create the interface in Common**

Create `src/PassReset.Common/IRecaptchaVerifier.cs` with the verbatim contents of the existing Web file, only changing the namespace:

```csharp
namespace PassReset.Common;

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

- [ ] **Step 2: Delete the Web copy**

Delete `src/PassReset.Web/Services/IRecaptchaVerifier.cs`.

- [ ] **Step 3: Fix the implementation's using**

In `src/PassReset.Web/Services/GoogleRecaptchaVerifier.cs`, ensure `using PassReset.Common;` is present at the top (add it if absent). The class still declares `: IRecaptchaVerifier` and lives in `namespace PassReset.Web.Services;`.

- [ ] **Step 4: Build to verify the move compiles**

Run: `dotnet build src/PassReset.sln -c Release`
Expected: Build succeeds. If `PasswordController.cs` or other Web files reference `IRecaptchaVerifier` via `PassReset.Web.Services`, they already have `using PassReset.Web.Services;` — add `using PassReset.Common;` where the compiler reports CS0246. (PasswordController already imports `PassReset.Common`.)

- [ ] **Step 5: Run the safety-net tests**

Run: `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj -c Release`
Expected: PASS — all existing tests green (recaptcha is disabled in `TestConfig`, but the type must still resolve).

- [ ] **Step 6: Commit**

```bash
git add src/PassReset.Common/IRecaptchaVerifier.cs src/PassReset.Web/Services/GoogleRecaptchaVerifier.cs
git rm src/PassReset.Web/Services/IRecaptchaVerifier.cs
git commit -m "refactor(common): relocate IRecaptchaVerifier to Common"
```

---

### Task 2: Relocate `ISiemService`, `SiemEventType`, and `AuditEvent` to `PassReset.Common`

**Files:**
- Create: `src/PassReset.Common/ISiemService.cs`
- Create: `src/PassReset.Common/AuditEvent.cs`
- Modify: `src/PassReset.Web/Services/ISiemService.cs` (delete)
- Modify: `src/PassReset.Web/Services/AuditEvent.cs` (delete)
- Modify: `src/PassReset.Web/Services/SiemService.cs` (add using)

**Interfaces:**
- Produces: `PassReset.Common.SiemEventType` (enum, all 12 members verbatim), `PassReset.Common.AuditEvent` (record), `PassReset.Common.ISiemService` with `void LogEvent(SiemEventType, string, string, string?)` and `void LogEvent(AuditEvent)`.

- [ ] **Step 1: Create `ISiemService.cs` in Common**

Create `src/PassReset.Common/ISiemService.cs` with the verbatim enum + interface from the Web file, changing only the namespace to `PassReset.Common`. Copy all 12 `SiemEventType` members exactly (`PasswordChanged`, `InvalidCredentials`, `UserNotFound`, `PortalLockout`, `ApproachingLockout`, `RateLimitExceeded`, `RecaptchaFailed`, `ChangeNotPermitted`, `ValidationFailed`, `Generic`, `PasswordChangeAttemptStarted`, `StatusChecked`) with their XML doc comments. The interface body:

```csharp
public interface ISiemService
{
    /// <summary>Records a security event synchronously (no async I/O on the hot path).</summary>
    void LogEvent(SiemEventType eventType, string username, string ipAddress, string? detail = null);

    /// <summary>
    /// STAB-015: Records a structured audit event via the allowlist <see cref="AuditEvent"/>
    /// DTO (D-10). Emits an RFC 5424 STRUCTURED-DATA element through the configured SD-ID.
    /// Must not throw — hot-path invariant identical to the legacy overload.
    /// </summary>
    void LogEvent(AuditEvent evt);
}
```

- [ ] **Step 2: Create `AuditEvent.cs` in Common**

Create `src/PassReset.Common/AuditEvent.cs` with the verbatim record from the Web file (including the STAB-015 redaction doc comment that warns against adding secret fields), changing only the namespace to `PassReset.Common`:

```csharp
namespace PassReset.Common;

/// <summary>
/// STAB-015 (D-10) allowlist DTO for audit events. No secret fields exist on this
/// type by design — compile-time redaction. Do NOT add Password, Token, PrivateKey,
/// Secret, or ApiKey properties; doing so violates STAB-015's redaction guarantee
/// and breaks the reflection test in AuditEventRedactionTests.
/// </summary>
public sealed record AuditEvent(
    SiemEventType EventType,
    string Outcome,
    string Username,
    string? ClientIp = null,
    string? TraceId = null,
    string? Detail = null);
```

- [ ] **Step 3: Delete the Web copies**

Delete `src/PassReset.Web/Services/ISiemService.cs` and `src/PassReset.Web/Services/AuditEvent.cs`.

- [ ] **Step 4: Fix the implementation's using**

In `src/PassReset.Web/Services/SiemService.cs`, add `using PassReset.Common;` to the top (the file currently has `using PassReset.Web.Models;`). The class stays `internal sealed class SiemService : ISiemService, IDisposable` in `namespace PassReset.Web.Services;`.

- [ ] **Step 5: Build and fix references**

Run: `dotnet build src/PassReset.sln -c Release`
Expected: Build succeeds. The compiler will flag any file using `SiemEventType` / `AuditEvent` / `ISiemService` via the old namespace. Add `using PassReset.Common;` at each CS0246 site. Known call sites to check: `PasswordController.cs` (already imports `PassReset.Common`), `PasswordExpiryNotificationService.cs`, `HealthController.cs`, `Program.cs`, and the test file `GenericErrorMappingTests.cs` (imports both `PassReset.Common` and `PassReset.Web.Services` — leave it; `using PassReset.Web.Services;` is harmless once the types resolve from Common).

- [ ] **Step 6: Run the safety-net tests**

Run: `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj -c Release`
Expected: PASS — including `Production_InvalidCredentials_SiemRemainsGranular` and the `AuditEvent`/TraceId tests, which prove the SIEM types still bind correctly after the move.

- [ ] **Step 7: Commit**

```bash
git add src/PassReset.Common/ISiemService.cs src/PassReset.Common/AuditEvent.cs src/PassReset.Web/Services/SiemService.cs
git rm src/PassReset.Web/Services/ISiemService.cs src/PassReset.Web/Services/AuditEvent.cs
git commit -m "refactor(common): relocate ISiemService and AuditEvent to Common"
```

---

### Task 3: Add the `IErrorRedactor` seam (Candidate 2, folded in)

**Files:**
- Create: `src/PassReset.Common/ChangeFlow/IErrorRedactor.cs`
- Create: `src/PassReset.Web/Services/HostEnvironmentErrorRedactor.cs`
- Test: `src/PassReset.Tests/ChangeFlow/ErrorRedactorTests.cs`

**Interfaces:**
- Produces: `PassReset.Common.ChangeFlow.IErrorRedactor` with `ApiErrorItem Redact(ApiErrorItem error)` and `static bool IsAccountEnumerationCode(ApiErrorCode code)`. The Web adapter `HostEnvironmentErrorRedactor` reads `IHostEnvironment.IsProduction()`.

- [ ] **Step 1: Write the failing test for the redaction rule**

Create `src/PassReset.Tests/ChangeFlow/ErrorRedactorTests.cs`. The test uses a tiny hand-fake redactor that is constructed with a `bool redactInProduction`-style flag — but since `IErrorRedactor` hides the env decision, we test a concrete cross-platform implementation `FixedModeErrorRedactor` that the test defines inline, plus the static classifier:

```csharp
using PassReset.Common;
using PassReset.Common.ChangeFlow;
using Xunit;

namespace PassReset.Tests.ChangeFlow;

public class ErrorRedactorTests
{
    // A cross-platform redactor that mirrors the production rule without IHostEnvironment.
    private sealed class FixedModeErrorRedactor(bool redact) : IErrorRedactor
    {
        public ApiErrorItem Redact(ApiErrorItem error) =>
            redact && IErrorRedactor.IsAccountEnumerationCode(error.ErrorCode)
                ? new ApiErrorItem(ApiErrorCode.Generic, error.Message)
                : error;
    }

    [Theory]
    [InlineData(ApiErrorCode.InvalidCredentials, true)]
    [InlineData(ApiErrorCode.UserNotFound, true)]
    [InlineData(ApiErrorCode.ApproachingLockout, false)]
    [InlineData(ApiErrorCode.PortalLockout, false)]
    [InlineData(ApiErrorCode.ChangeNotPermitted, false)]
    public void IsAccountEnumerationCode_ClassifiesCorrectly(ApiErrorCode code, bool expected) =>
        Assert.Equal(expected, IErrorRedactor.IsAccountEnumerationCode(code));

    [Fact]
    public void Redact_WhenRedacting_CollapsesInvalidCredentialsToGeneric()
    {
        var redactor = new FixedModeErrorRedactor(redact: true);
        var result = redactor.Redact(new ApiErrorItem(ApiErrorCode.InvalidCredentials));
        Assert.Equal(ApiErrorCode.Generic, result.ErrorCode);
    }

    [Fact]
    public void Redact_WhenRedacting_PreservesLockoutCodes()
    {
        var redactor = new FixedModeErrorRedactor(redact: true);
        var result = redactor.Redact(new ApiErrorItem(ApiErrorCode.ApproachingLockout));
        Assert.Equal(ApiErrorCode.ApproachingLockout, result.ErrorCode);
    }

    [Fact]
    public void Redact_WhenNotRedacting_PreservesInvalidCredentials()
    {
        var redactor = new FixedModeErrorRedactor(redact: false);
        var result = redactor.Redact(new ApiErrorItem(ApiErrorCode.InvalidCredentials));
        Assert.Equal(ApiErrorCode.InvalidCredentials, result.ErrorCode);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release --filter ErrorRedactorTests`
Expected: FAIL — `IErrorRedactor` does not exist (CS0246 / build error).

- [ ] **Step 3: Create the `IErrorRedactor` interface with the static classifier**

Create `src/PassReset.Common/ChangeFlow/IErrorRedactor.cs`:

```csharp
namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The Error Redaction seam (STAB-013): decides whether a precise failure code may reach
/// the client or must collapse to <see cref="ApiErrorCode.Generic"/>. Decoupled from the
/// hosting concept ("are we in production?") that triggers it — that lives in the adapter.
/// </summary>
public interface IErrorRedactor
{
    /// <summary>
    /// Returns the error to put on the wire: the original, or a Generic collapse when the
    /// adapter's policy says enumeration codes must be redacted in the current environment.
    /// SIEM granularity is preserved independently of this result.
    /// </summary>
    ApiErrorItem Redact(ApiErrorItem error);

    /// <summary>
    /// STAB-013 D-01: account-enumeration codes that leak whether a username exists in AD.
    /// <see cref="ApiErrorCode.InvalidCredentials"/> and <see cref="ApiErrorCode.UserNotFound"/>
    /// are enumeration vectors. ApproachingLockout/PortalLockout are NOT — they leak only
    /// per-account portal throttling state, never directory membership.
    /// </summary>
    static bool IsAccountEnumerationCode(ApiErrorCode code) =>
        code is ApiErrorCode.InvalidCredentials or ApiErrorCode.UserNotFound;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release --filter ErrorRedactorTests`
Expected: PASS — 8 cases (1 theory × 5 InlineData + 3 facts).

- [ ] **Step 5: Create the Web adapter that reads `IHostEnvironment`**

Create `src/PassReset.Web/Services/HostEnvironmentErrorRedactor.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using PassReset.Common;
using PassReset.Common.ChangeFlow;

namespace PassReset.Web.Services;

/// <summary>
/// STAB-013 adapter: applies the enumeration-code collapse only in the Production
/// environment, matching the behavior previously inlined in PasswordController.
/// </summary>
public sealed class HostEnvironmentErrorRedactor(IHostEnvironment environment) : IErrorRedactor
{
    private readonly IHostEnvironment _environment = environment;

    public ApiErrorItem Redact(ApiErrorItem error) =>
        _environment.IsProduction() && IErrorRedactor.IsAccountEnumerationCode(error.ErrorCode)
            ? new ApiErrorItem(ApiErrorCode.Generic, error.Message)
            : error;
}
```

- [ ] **Step 6: Build to verify the adapter compiles**

Run: `dotnet build src/PassReset.sln -c Release`
Expected: Build succeeds (the adapter isn't wired into DI yet — that happens in Task 6).

- [ ] **Step 7: Commit**

```bash
git add src/PassReset.Common/ChangeFlow/IErrorRedactor.cs src/PassReset.Web/Services/HostEnvironmentErrorRedactor.cs src/PassReset.Tests/ChangeFlow/ErrorRedactorTests.cs
git commit -m "feat(common): add IErrorRedactor seam for STAB-013 enumeration redaction"
```

---

### Task 4: Define the flow's data types — `ChangePasswordRequest`, `RequestContext`, `ChangePasswordOutcome`, settings, interface

**Files:**
- Create: `src/PassReset.Common/ChangeFlow/ChangePasswordRequest.cs`
- Create: `src/PassReset.Common/ChangeFlow/ChangePasswordOutcome.cs`
- Create: `src/PassReset.Common/ChangeFlow/IChangeFlowSettings.cs`
- Create: `src/PassReset.Common/ChangeFlow/IChangePasswordFlow.cs`

**Interfaces:**
- Produces (relied on by Task 5 and Task 6):
  - `record ChangePasswordRequest(string Username, string CurrentPassword, string NewPassword)`
  - `record RequestContext(string ClientIp, string TraceId)`
  - `enum Disposition { Ok, ValidationError, CaptchaRejected, ChangeFailed }`
  - `record NotificationRequest(string Username, string Timestamp, string ClientIp)`
  - `record ChangePasswordOutcome(Disposition Disposition, ApiErrorItem? Error, string? SuccessMessage, NotificationRequest? Notification)` with static factories `Success(string message, NotificationRequest? notify)`, `Validation(ApiErrorItem error)`, `Captcha(ApiErrorItem error)`, `Changed(ApiErrorItem error)`.
  - `interface IChangeFlowSettings { int MinimumDistance; bool RecaptchaEnabled; string RecaptchaAction; bool NotificationEnabled; }`
  - `interface IChangePasswordFlow { Task<ChangePasswordOutcome> HandleAsync(ChangePasswordRequest request, RequestContext context); }`

- [ ] **Step 1: Create the request + context records**

Create `src/PassReset.Common/ChangeFlow/ChangePasswordRequest.cs`:

```csharp
namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The validated inputs to a Password Change, mapped by the controller from its
/// framework-bound model after ModelState validation has already passed.
/// </summary>
public sealed record ChangePasswordRequest(string Username, string CurrentPassword, string NewPassword)
{
    /// <summary>The reCAPTCHA token from the client (empty when reCAPTCHA is disabled).</summary>
    public string Recaptcha { get; init; } = string.Empty;
}

/// <summary>
/// Hosting-flavored correlation values passed in by the controller so the flow never
/// reaches for ASP.NET ambient state (Activity.Current, HttpContext).
/// </summary>
public sealed record RequestContext(string ClientIp, string TraceId);
```

- [ ] **Step 2: Create the outcome record + disposition + notification request**

Create `src/PassReset.Common/ChangeFlow/ChangePasswordOutcome.cs`:

```csharp
namespace PassReset.Common.ChangeFlow;

/// <summary>How the Change Flow concluded — drives the controller's HTTP mapping.</summary>
public enum Disposition
{
    /// <summary>Change succeeded.</summary>
    Ok,
    /// <summary>A flow-owned validation rule rejected the request (e.g. minimum distance).</summary>
    ValidationError,
    /// <summary>reCAPTCHA verification failed.</summary>
    CaptchaRejected,
    /// <summary>The credentialed change itself failed (provider returned an error).</summary>
    ChangeFailed,
}

/// <summary>
/// The intent to send a password-changed notification. Carries inputs only — the
/// controller resolves the recipient and renders the body off the response path so the
/// synchronous directory lookup never blocks the HTTP response.
/// </summary>
public sealed record NotificationRequest(string Username, string Timestamp, string ClientIp);

/// <summary>
/// The full result of running the Change Flow. The <see cref="Error"/> is ALREADY redacted
/// per the Error Redaction seam — the controller serializes it verbatim.
/// </summary>
public sealed record ChangePasswordOutcome(
    Disposition Disposition,
    ApiErrorItem? Error,
    string? SuccessMessage,
    NotificationRequest? Notification)
{
    public static ChangePasswordOutcome Success(string message, NotificationRequest? notify) =>
        new(Disposition.Ok, null, message, notify);

    public static ChangePasswordOutcome Validation(ApiErrorItem error) =>
        new(Disposition.ValidationError, error, null, null);

    public static ChangePasswordOutcome Captcha(ApiErrorItem error) =>
        new(Disposition.CaptchaRejected, error, null, null);

    public static ChangePasswordOutcome Changed(ApiErrorItem error) =>
        new(Disposition.ChangeFailed, error, null, null);
}
```

- [ ] **Step 3: Create the minimal settings interface**

Create `src/PassReset.Common/ChangeFlow/IChangeFlowSettings.cs`:

```csharp
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
```

- [ ] **Step 4: Create the flow interface**

Create `src/PassReset.Common/ChangeFlow/IChangePasswordFlow.cs`:

```csharp
namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The Change Flow: the full server-side sequence above the Password Changer seam.
/// Owns minimum-distance validation, the reCAPTCHA gate, the credentialed change,
/// Error Redaction, and the SIEM audit emitted at each decision point. Returns a
/// fully-resolved <see cref="ChangePasswordOutcome"/>; performs no HTTP and no email I/O.
/// </summary>
public interface IChangePasswordFlow
{
    Task<ChangePasswordOutcome> HandleAsync(ChangePasswordRequest request, RequestContext context);
}
```

- [ ] **Step 5: Build to verify the types compile**

Run: `dotnet build src/PassReset.Common/PassReset.Common.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/PassReset.Common/ChangeFlow/ChangePasswordRequest.cs src/PassReset.Common/ChangeFlow/ChangePasswordOutcome.cs src/PassReset.Common/ChangeFlow/IChangeFlowSettings.cs src/PassReset.Common/ChangeFlow/IChangePasswordFlow.cs
git commit -m "feat(common): add Change Flow request/outcome types and interface"
```

---

### Task 5: Implement `ChangePasswordFlow` (the deep module) test-first

**Files:**
- Create: `src/PassReset.Common/ChangeFlow/ChangePasswordFlow.cs`
- Test: `src/PassReset.Tests/ChangeFlow/ChangePasswordFlowTests.cs`

**Interfaces:**
- Consumes: `IPasswordChanger` (`Task<ApiErrorItem?> PerformPasswordChangeAsync(string, string, string)`), `IRecaptchaVerifier`, `ISiemService`, `IErrorRedactor`, `IChangeFlowSettings`, `PasswordDistance.Levenshtein(string, string)` (static, in `PassReset.Common`).
- Produces: `sealed class ChangePasswordFlow : IChangePasswordFlow` with constructor `(IPasswordChanger changer, IRecaptchaVerifier recaptcha, ISiemService siem, IErrorRedactor redactor, IChangeFlowSettings settings)`.

**Sequence the flow must implement (mirrors PasswordController.PostAsync lines 147–228 exactly):**
1. Emit `PasswordChangeAttemptStarted` audit (outcome `"AttemptStarted"`).
2. If `settings.MinimumDistance > 0` and `Levenshtein(current, new) < MinimumDistance`: emit audit `"DistanceTooLow"` (NO SiemEventType — matches today's `Audit("DistanceTooLow", …)` with no event), return `Validation(new ApiErrorItem(ApiErrorCode.MinimumDistance))`.
3. If `settings.RecaptchaEnabled` and `!await recaptcha.VerifyAsync(req.Recaptcha, settings.RecaptchaAction, ctx.ClientIp)`: emit `RecaptchaFailed` audit (outcome `"RecaptchaFailed"`), return `Captcha(new ApiErrorItem(ApiErrorCode.InvalidCaptcha))`.
4. `var error = await changer.PerformPasswordChangeAsync(req.Username, req.CurrentPassword, req.NewPassword)`.
5. If `error is not null`: emit audit `"Failed:{error.ErrorCode}"` with `MapErrorCodeToSiemEvent(error.ErrorCode)` and `error.Message` as detail, return `Changed(redactor.Redact(error))`.
6. Emit `PasswordChanged` audit (outcome `"Success"`). Build `NotificationRequest` if `settings.NotificationEnabled`, else null. Return `Success("Password changed successfully.", notify)`.

The `MapErrorCodeToSiemEvent` switch moves into the flow (private static), identical to the controller's: `InvalidCredentials→InvalidCredentials`, `UserNotFound→UserNotFound`, `PortalLockout→PortalLockout`, `ApproachingLockout→ApproachingLockout`, `ChangeNotPermitted→ChangeNotPermitted`, default `Generic`.

The audit emission uses the structured overload `siem.LogEvent(new AuditEvent(EventType, Outcome, Username, ClientIp, TraceId, Detail))` — matching the controller's `Audit()` helper which fires the structured event whenever a `SiemEventType` is present. The timestamp for the notification uses `DateTime.UtcNow.ToString("u")` (same format as the controller).

- [ ] **Step 1: Write the failing tests**

Create `src/PassReset.Tests/ChangeFlow/ChangePasswordFlowTests.cs`. Uses NSubstitute (already referenced) for the seams and a hand `TestSettings` for `IChangeFlowSettings`. A recording redactor proves the flow redacts via the seam (not inline).

```csharp
using NSubstitute;
using PassReset.Common;
using PassReset.Common.ChangeFlow;
using Xunit;

namespace PassReset.Tests.ChangeFlow;

public class ChangePasswordFlowTests
{
    private sealed class TestSettings : IChangeFlowSettings
    {
        public int MinimumDistance { get; init; }
        public bool RecaptchaEnabled { get; init; }
        public string RecaptchaAction { get; init; } = "change_password";
        public bool NotificationEnabled { get; init; }
    }

    // Passthrough redactor: proves the flow calls the seam without applying env rules here.
    private sealed class PassthroughRedactor : IErrorRedactor
    {
        public int Calls { get; private set; }
        public ApiErrorItem Redact(ApiErrorItem error) { Calls++; return error; }
    }

    private static ChangePasswordRequest Req(string user = "alice", string cur = "OldPass1!", string @new = "BrandNewP@ss123") =>
        new(user, cur, @new) { Recaptcha = "tok" };

    private static RequestContext Ctx() => new(ClientIp: "10.0.0.1", TraceId: "trace-123");

    private static (ChangePasswordFlow flow, IPasswordChanger changer, IRecaptchaVerifier recaptcha,
        ISiemService siem, PassthroughRedactor redactor) Build(IChangeFlowSettings settings)
    {
        var changer = Substitute.For<IPasswordChanger>();
        var recaptcha = Substitute.For<IRecaptchaVerifier>();
        var siem = Substitute.For<ISiemService>();
        var redactor = new PassthroughRedactor();
        var flow = new ChangePasswordFlow(changer, recaptcha, siem, redactor, settings);
        return (flow, changer, recaptcha, siem, redactor);
    }

    [Fact]
    public async Task HappyPath_ReturnsOk_AndEmitsPasswordChanged()
    {
        var (flow, changer, _, siem, _) = Build(new TestSettings());
        changer.PerformPasswordChangeAsync("alice", "OldPass1!", "BrandNewP@ss123")
            .Returns((ApiErrorItem?)null);

        var outcome = await flow.HandleAsync(Req(), Ctx());

        Assert.Equal(Disposition.Ok, outcome.Disposition);
        Assert.Equal("Password changed successfully.", outcome.SuccessMessage);
        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.EventType == SiemEventType.PasswordChanged));
    }

    [Fact]
    public async Task HappyPath_WhenNotificationEnabled_ReturnsNotificationRequest()
    {
        var (flow, changer, _, _, _) = Build(new TestSettings { NotificationEnabled = true });
        changer.PerformPasswordChangeAsync(default!, default!, default!).ReturnsForAnyArgs((ApiErrorItem?)null);

        var outcome = await flow.HandleAsync(Req(user: "bob"), Ctx());

        Assert.NotNull(outcome.Notification);
        Assert.Equal("bob", outcome.Notification!.Username);
        Assert.Equal("10.0.0.1", outcome.Notification.ClientIp);
    }

    [Fact]
    public async Task HappyPath_WhenNotificationDisabled_NoNotificationRequest()
    {
        var (flow, changer, _, _, _) = Build(new TestSettings { NotificationEnabled = false });
        changer.PerformPasswordChangeAsync(default!, default!, default!).ReturnsForAnyArgs((ApiErrorItem?)null);

        var outcome = await flow.HandleAsync(Req(), Ctx());

        Assert.Null(outcome.Notification);
    }

    [Fact]
    public async Task DistanceTooLow_ReturnsValidation_WithoutCallingChanger()
    {
        var (flow, changer, _, _, _) = Build(new TestSettings { MinimumDistance = 50 });

        var outcome = await flow.HandleAsync(Req(cur: "samePassword1!", @new: "samePassword2!"), Ctx());

        Assert.Equal(Disposition.ValidationError, outcome.Disposition);
        Assert.Equal(ApiErrorCode.MinimumDistance, outcome.Error!.ErrorCode);
        await changer.DidNotReceiveWithAnyArgs().PerformPasswordChangeAsync(default!, default!, default!);
    }

    [Fact]
    public async Task RecaptchaFails_ReturnsCaptchaRejected_WithoutCallingChanger()
    {
        var (flow, changer, recaptcha, siem, _) = Build(new TestSettings { RecaptchaEnabled = true });
        recaptcha.VerifyAsync("tok", "change_password", "10.0.0.1").Returns(false);

        var outcome = await flow.HandleAsync(Req(), Ctx());

        Assert.Equal(Disposition.CaptchaRejected, outcome.Disposition);
        Assert.Equal(ApiErrorCode.InvalidCaptcha, outcome.Error!.ErrorCode);
        await changer.DidNotReceiveWithAnyArgs().PerformPasswordChangeAsync(default!, default!, default!);
        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.EventType == SiemEventType.RecaptchaFailed));
    }

    [Fact]
    public async Task ChangeFails_ReturnsChangeFailed_AndRedactsThroughSeam()
    {
        var (flow, changer, _, siem, redactor) = Build(new TestSettings());
        changer.PerformPasswordChangeAsync(default!, default!, default!)
            .ReturnsForAnyArgs(new ApiErrorItem(ApiErrorCode.InvalidCredentials));

        var outcome = await flow.HandleAsync(Req(), Ctx());

        Assert.Equal(Disposition.ChangeFailed, outcome.Disposition);
        Assert.Equal(1, redactor.Calls); // proves redaction goes through the seam, not inline
        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.EventType == SiemEventType.InvalidCredentials));
    }

    [Fact]
    public async Task ChangeFails_AuditCarriesTraceIdFromContext()
    {
        var (flow, changer, _, siem, _) = Build(new TestSettings());
        changer.PerformPasswordChangeAsync(default!, default!, default!)
            .ReturnsForAnyArgs(new ApiErrorItem(ApiErrorCode.UserNotFound));

        await flow.HandleAsync(Req(), Ctx());

        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.TraceId == "trace-123" && e.ClientIp == "10.0.0.1"));
    }

    [Fact]
    public async Task EntryAlwaysEmitsAttemptStarted()
    {
        var (flow, changer, _, siem, _) = Build(new TestSettings());
        changer.PerformPasswordChangeAsync(default!, default!, default!).ReturnsForAnyArgs((ApiErrorItem?)null);

        await flow.HandleAsync(Req(), Ctx());

        siem.Received().LogEvent(Arg.Is<AuditEvent>(e => e.EventType == SiemEventType.PasswordChangeAttemptStarted));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release --filter ChangePasswordFlowTests`
Expected: FAIL — `ChangePasswordFlow` does not exist.

- [ ] **Step 3: Implement `ChangePasswordFlow`**

Create `src/PassReset.Common/ChangeFlow/ChangePasswordFlow.cs`:

```csharp
namespace PassReset.Common.ChangeFlow;

/// <summary>
/// The Change Flow implementation. Owns the full sequence above the Password Changer seam.
/// Performs no HTTP and no email I/O — it returns intent (see <see cref="NotificationRequest"/>).
/// </summary>
public sealed class ChangePasswordFlow(
    IPasswordChanger changer,
    IRecaptchaVerifier recaptcha,
    ISiemService siem,
    IErrorRedactor redactor,
    IChangeFlowSettings settings) : IChangePasswordFlow
{
    private readonly IPasswordChanger _changer = changer;
    private readonly IRecaptchaVerifier _recaptcha = recaptcha;
    private readonly ISiemService _siem = siem;
    private readonly IErrorRedactor _redactor = redactor;
    private readonly IChangeFlowSettings _settings = settings;

    public async Task<ChangePasswordOutcome> HandleAsync(ChangePasswordRequest request, RequestContext context)
    {
        Audit(SiemEventType.PasswordChangeAttemptStarted, "AttemptStarted", request, context);

        if (_settings.MinimumDistance > 0 &&
            PasswordDistance.Levenshtein(request.CurrentPassword, request.NewPassword) < _settings.MinimumDistance)
        {
            // Matches the controller: "DistanceTooLow" logged WITHOUT a SIEM event type.
            return ChangePasswordOutcome.Validation(new ApiErrorItem(ApiErrorCode.MinimumDistance));
        }

        if (_settings.RecaptchaEnabled &&
            !await _recaptcha.VerifyAsync(request.Recaptcha, _settings.RecaptchaAction, context.ClientIp))
        {
            Audit(SiemEventType.RecaptchaFailed, "RecaptchaFailed", request, context);
            return ChangePasswordOutcome.Captcha(new ApiErrorItem(ApiErrorCode.InvalidCaptcha));
        }

        var error = await _changer.PerformPasswordChangeAsync(
            request.Username, request.CurrentPassword, request.NewPassword);

        if (error is not null)
        {
            Audit(MapErrorCodeToSiemEvent(error.ErrorCode), $"Failed:{error.ErrorCode}", request, context, error.Message);
            return ChangePasswordOutcome.Changed(_redactor.Redact(error));
        }

        Audit(SiemEventType.PasswordChanged, "Success", request, context);

        var notify = _settings.NotificationEnabled
            ? new NotificationRequest(request.Username, DateTime.UtcNow.ToString("u"), context.ClientIp)
            : null;

        return ChangePasswordOutcome.Success("Password changed successfully.", notify);
    }

    private void Audit(SiemEventType eventType, string outcome, ChangePasswordRequest request,
        RequestContext context, string? detail = null) =>
        _siem.LogEvent(new AuditEvent(
            EventType: eventType,
            Outcome:   outcome,
            Username:  request.Username,
            ClientIp:  context.ClientIp,
            TraceId:   context.TraceId,
            Detail:    detail));

    private static SiemEventType MapErrorCodeToSiemEvent(ApiErrorCode code) => code switch
    {
        ApiErrorCode.InvalidCredentials => SiemEventType.InvalidCredentials,
        ApiErrorCode.UserNotFound       => SiemEventType.UserNotFound,
        ApiErrorCode.PortalLockout      => SiemEventType.PortalLockout,
        ApiErrorCode.ApproachingLockout => SiemEventType.ApproachingLockout,
        ApiErrorCode.ChangeNotPermitted => SiemEventType.ChangeNotPermitted,
        _                               => SiemEventType.Generic,
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release --filter ChangePasswordFlowTests`
Expected: PASS — all 8 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/PassReset.Common/ChangeFlow/ChangePasswordFlow.cs src/PassReset.Tests/ChangeFlow/ChangePasswordFlowTests.cs
git commit -m "feat(common): implement ChangePasswordFlow deep module"
```

---

### Task 6: Wire the flow into DI and rewrite `PasswordController.PostAsync` as a thin adapter

**Files:**
- Create: `src/PassReset.Web/Services/ChangeFlowSettingsAdapter.cs`
- Modify: `src/PassReset.Web/Program.cs` (register `IErrorRedactor`, `IChangeFlowSettings`, `IChangePasswordFlow`)
- Modify: `src/PassReset.Web/Controllers/PasswordController.cs:145-229` (PostAsync) and remove the now-flow-owned private helpers `MapErrorCodeToSiemEvent`, `IsAccountEnumerationCode`, `RedactIfProduction`

**Interfaces:**
- Consumes: `IChangePasswordFlow`, `ChangePasswordRequest`, `RequestContext`, `ChangePasswordOutcome`, `Disposition`, `IErrorRedactor`, `IChangeFlowSettings`.
- Produces: `sealed class ChangeFlowSettingsAdapter : IChangeFlowSettings` constructed from `IOptions<ClientSettings>`.

- [ ] **Step 1: Create the settings adapter**

Create `src/PassReset.Web/Services/ChangeFlowSettingsAdapter.cs`. `RecaptchaEnabled` mirrors the controller's exact guard (`Enabled == true && PrivateKey present`):

```csharp
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
```

- [ ] **Step 2: Register the three services in Program.cs**

In `src/PassReset.Web/Program.cs`, after the SIEM registration (`builder.Services.AddSingleton<ISiemService, SiemService>();` around line 430), add:

```csharp
    // ─── Change Flow (deep module) + its seams ───────────────────────────────────
    builder.Services.AddSingleton<IErrorRedactor, HostEnvironmentErrorRedactor>();
    builder.Services.AddSingleton<IChangeFlowSettings, ChangeFlowSettingsAdapter>();
    builder.Services.AddSingleton<IChangePasswordFlow>(sp => new ChangePasswordFlow(
        sp.GetRequiredService<IPasswordChanger>(),
        sp.GetRequiredService<IRecaptchaVerifier>(),
        sp.GetRequiredService<ISiemService>(),
        sp.GetRequiredService<IErrorRedactor>(),
        sp.GetRequiredService<IChangeFlowSettings>()));
```

Add `using PassReset.Common.ChangeFlow;` to the top of `Program.cs` if not present. Note: `IErrorRedactor` depends on `IHostEnvironment`, which ASP.NET registers by default — no extra wiring needed.

- [ ] **Step 3: Rewrite PostAsync as a thin adapter**

In `src/PassReset.Web/Controllers/PasswordController.cs`:

(a) Add `using PassReset.Common.ChangeFlow;` to the top.

(b) Add `IChangePasswordFlow _changeFlow` as a constructor dependency (add the field, constructor parameter, and assignment). You may now REMOVE these constructor dependencies that PostAsync no longer uses directly — BUT `StatusAsync` and the other endpoints still use several. Keep all existing fields for now (StatusAsync still needs `_statusReader`, `_recaptchaVerifier`, `_siemService`, `_hostEnvironment`); only `PostAsync`'s direct usage changes. Do NOT remove fields still referenced elsewhere — the build will tell you. (`_changer` is still used by nothing else after this; leave it — Task 7 note covers cleanup.)

(c) Replace the body of `PostAsync` (lines 145–229) with:

```csharp
    public async Task<IActionResult> PostAsync([FromBody] ChangePasswordModel model)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!ModelState.IsValid)
        {
            _siemService.LogEvent(SiemEventType.ValidationFailed, model.Username, clientIp);
            return BadRequest(ApiResult.FromModelStateErrors(ModelState));
        }

        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "unknown";
        using var requestScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Username"] = model.Username,
            ["TraceId"] = traceId,
            ["ClientIp"] = clientIp,
        });

        var request = new ChangePasswordRequest(model.Username, model.CurrentPassword, model.NewPassword)
        {
            Recaptcha = model.Recaptcha,
        };

        var outcome = await _changeFlow.HandleAsync(request, new RequestContext(clientIp, traceId));

        if (outcome.Disposition != Disposition.Ok)
        {
            var result = new ApiResult();
            result.Errors.Add(outcome.Error!);   // already redacted by the flow's IErrorRedactor
            return BadRequest(result);
        }

        if (outcome.Notification is { } notify)
            FireNotification(notify);

        return Ok(new ApiResult(outcome.SuccessMessage));
    }

    /// <summary>
    /// Fire-and-forget password-changed email. The directory lookup + templating run OFF the
    /// HTTP response path (Task.Run) so SMTP/LDAP latency never blocks the response — identical
    /// to the pre-refactor behavior.
    /// </summary>
    private void FireNotification(NotificationRequest notify)
    {
        var emailSvc = _emailService;
        var notifCfg = _emailNotifSettings.Value;
        var directoryReader = _directoryReader;

        _ = Task.Run(async () =>
        {
            var emailAddress = directoryReader.GetUserEmail(notify.Username);
            if (string.IsNullOrWhiteSpace(emailAddress)) return;

            var body = notifCfg.BodyTemplate
                .Replace("{Username}",  notify.Username,  StringComparison.Ordinal)
                .Replace("{Timestamp}", notify.Timestamp, StringComparison.Ordinal)
                .Replace("{IpAddress}", notify.ClientIp,  StringComparison.Ordinal);

            await emailSvc.SendAsync(emailAddress, notify.Username, notifCfg.Subject, body);
        });
    }
```

(d) Note on the `ValidationFailed` SIEM emission: today the controller emits `SiemEventType.ValidationFailed` via `Audit()` for the ModelState branch. The simple `LogEvent(SiemEventType, …)` overload above preserves the event. The structured-audit-with-TraceId test (`Production_InvalidCredentials_EmitsStructuredAuditWithTraceId`) targets the InvalidCredentials path (now in the flow), not the ModelState path, so this is fine.

(e) Leave `MapErrorCodeToSiemEvent`, `IsAccountEnumerationCode`, `RedactIfProduction` in place for now ONLY if `StatusAsync` still references them. `StatusAsync` references `MapErrorCodeToSiemEvent` (line 269) and `RedactIfProduction` (line 271) and `Audit` (multiple). Since this task does NOT touch StatusAsync, KEEP all three private helpers and the `Audit` helper. (They become dead only after Task 7 deepens Status — out of scope here.)

- [ ] **Step 4: Build and fix any reference errors**

Run: `dotnet build src/PassReset.sln -c Release`
Expected: Build succeeds. If the compiler warns that `_changer` is now unused by PostAsync but it's still a field — that's fine (no error; it's injected for the flow indirectly). If any unused-field analyzer fails the build, leave the field (it's referenced by DI registration, not removed).

- [ ] **Step 5: Run the full safety-net suite — THE critical gate**

Run: `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj -c Release`
Expected: PASS — ALL 11 `GenericErrorMappingTests` + all `StatusEndpointTests`. This proves behavior preservation end-to-end: redaction, SIEM granularity, AttemptStarted, TraceId, lockout codes. If ANY fails, STOP and diagnose — the refactor changed behavior.

- [ ] **Step 6: Run the cross-platform suite**

Run: `dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release`
Expected: PASS — flow + redactor unit tests plus all pre-existing tests.

- [ ] **Step 7: Commit**

```bash
git add src/PassReset.Web/Services/ChangeFlowSettingsAdapter.cs src/PassReset.Web/Program.cs src/PassReset.Web/Controllers/PasswordController.cs
git commit -m "refactor(web): route PostAsync through ChangePasswordFlow; controller becomes thin adapter"
```

---

### Task 7: Documentation pass

**Files:**
- Modify: `CLAUDE.md` (Architecture → "Password change request flow" section)
- Verify: `CONTEXT.md` already has the [[Change Flow]] and [[Error Redaction]] terms (added during design)

- [ ] **Step 1: Update the request-flow description in CLAUDE.md**

In `CLAUDE.md`, the "Password change request flow" section currently lists the inline steps `2. ModelState validation → Levenshtein distance check → reCAPTCHA v3 verify…` inside `PasswordController`. Update it to reflect the deepened design. Replace that flow description's steps 2–3 with:

```markdown
2. ModelState validation (controller, framework-bound) → maps to `ChangePasswordRequest`
3. `IChangePasswordFlow.HandleAsync()` owns the rest: minimum-distance check → reCAPTCHA verify → `IPasswordChanger.PerformPasswordChangeAsync()` (lockout decorator → AD) → `IErrorRedactor` (STAB-013) → SIEM audit per decision point, returning a `ChangePasswordOutcome`
4. Controller maps the outcome to `Ok`/`BadRequest(ApiResult)`; on success fires the email notification off the response path (`Task.Run`)
```

Also add a one-line note under the Architecture provider/seam discussion: `IChangePasswordFlow` (in `PassReset.Common`) is the deep module above the Change seam — see [[Change Flow]] in CONTEXT.md.

- [ ] **Step 2: Verify the build and full test suites one final time**

Run: `dotnet build src/PassReset.sln -c Release && dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release && dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj -c Release`
Expected: All green.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: describe deepened Change Flow in architecture notes"
```

---

## Notes on scope (deliberately deferred)

- **`StatusAsync` is untouched.** Per the design (grilling Q7), a parallel `IStatusCheckFlow` and any shared "authenticated-request preamble" extraction are a separate follow-up — built only when a third caller justifies the shared seam ("one adapter = hypothetical seam, two = real"). After this plan, `StatusAsync` still uses the controller's `Audit`/`MapErrorCodeToSiemEvent`/`RedactIfProduction` helpers; they remain live for it.
- **Notification templating stays in the controller** (`FireNotification`). The flow returns inputs only, keeping the synchronous `GetUserEmail` LDAP lookup off the response path. Extracting a pure template renderer is possible later without touching the flow.
- **`EmailNotificationSettings` does NOT move to Common** — the flow only needs the `Enabled` bool (via `IChangeFlowSettings.NotificationEnabled`); the controller owns Subject/BodyTemplate.
