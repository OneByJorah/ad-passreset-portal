# Carve IPasswordChangeProvider into Cohesive Seams — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the 7-method `IPasswordChangeProvider` interface with three cohesive seams — `IPasswordChanger`, `IPasswordStatusReader`, `IDirectoryUserReader` — and move the Levenshtein algorithm out to a free function.

**Architecture:** Pure, behavior-preserving refactor. The interface is split along its three caller clusters (credentialed write, credentialed read, unauthenticated directory read). One concrete directory adapter (`PasswordChangeProvider` / `LdapPasswordChangeProvider` / `DebugPasswordChangeProvider`) implements all three seams; only the change seam is wrapped by the `Lockout` and `LocalPolicy` decorators. DI registers the concrete adapter once and exposes it under the three seam interfaces. No production logic changes — only interface shape, consumer injection, and DI wiring. The compiler enforces completeness; the existing contract test (which only touches `PerformPasswordChangeAsync`) stays green.

**Tech Stack:** C# 13 / .NET 10 (`net10.0` for Common, `net10.0-windows` for provider + web), xUnit v3, ASP.NET Core DI.

## Global Constraints

- **No behavior change.** This is a refactor. Every existing test must stay green; no test assertions change.
- **Platform:** `PassReset.Common` targets `net10.0` (platform-neutral); the three new interfaces live there. Providers/web target `net10.0-windows`.
- **Naming (from CONTEXT.md → "Provider Seams"):** the three seams are the **Password Changer**, **Password Status Reader**, and **Directory User Reader** responsibilities. Interface names: `IPasswordChanger`, `IPasswordStatusReader`, `IDirectoryUserReader`. Avoid re-introducing a composed `IPasswordChangeProvider`.
- **Delete the old interface outright** — no composed marker interface that extends the three (it re-creates the wide seam).
- **Levenshtein** moves to a free function, not a seam — it never varies per adapter.
- **One adapter instance.** The concrete adapter holds AD context; DI must share a single instance across all three seam registrations.
- **Build commands:** `dotnet build src/PassReset.sln -c Release` · `dotnet test src/PassReset.sln`.
- **Commit convention:** `type(scope): subject`. Scope for this work: `refactor(provider)` / `refactor(web)` / `refactor(common)`.

---

## File Structure

**Create:**
- `src/PassReset.Common/IPasswordChanger.cs` — the credentialed write seam (1 method).
- `src/PassReset.Common/IPasswordStatusReader.cs` — the credentialed read seam (status + policy, 2 methods).
- `src/PassReset.Common/IDirectoryUserReader.cs` — the unauthenticated directory-read seam (email, group, maxAge, 3 methods).
- `src/PassReset.Common/PasswordDistance.cs` — `static int Levenshtein(string a, string b)`.

**Delete:**
- `src/PassReset.Common/IPasswordChangeProvider.cs`.

**Modify:**
- `src/PassReset.PasswordProvider/PasswordChangeProvider.cs:16` — implement the three seams instead of `IPasswordChangeProvider`.
- `src/PassReset.PasswordProvider.Ldap/LdapPasswordChangeProvider.cs:14` — same.
- `src/PassReset.Web/Helpers/DebugPasswordChangeProvider.cs:11` — same; drop its `MeasureNewPasswordDistance` forward.
- `src/PassReset.PasswordProvider/LockoutPasswordChangeProvider.cs:49` — implement only `IPasswordChanger` (+ existing `ILockoutDiagnostics, IDisposable`); delete the 5 forwarding methods (lines 135–150); inner type becomes `IPasswordChanger`.
- `src/PassReset.Common/LocalPolicy/LocalPolicyPasswordChangeProvider.cs:11` — implement only `IPasswordChanger`; delete the 6 forwarding methods (lines 54–68); inner type becomes `IPasswordChanger`.
- `src/PassReset.PasswordProvider/PasswordPolicyCache.cs:18,20` — inject `IPasswordStatusReader` instead of `IPasswordChangeProvider`.
- `src/PassReset.Web/Controllers/PasswordController.cs` — inject `IPasswordChanger` + `IPasswordStatusReader` + `IDirectoryUserReader`; call `PasswordDistance.Levenshtein` directly at line 156.
- `src/PassReset.Web/Services/PasswordExpiryNotificationService.cs:86,180` — resolve/accept `IDirectoryUserReader`.
- `src/PassReset.Web/Program.cs:286–408` — register the concrete adapter once, map the three seams, wrap only the change seam.
- `src/PassReset.Tests/Contracts/IPasswordChangeProviderContract.cs` — `CreateProvider`/`SeedBannedWord` return `IPasswordChanger`; file/class may be renamed to `IPasswordChangerContract`.
- Provider-specific test files and fakes that declare `: IPasswordChangeProvider` — retarget to the three seams (mechanical; the compiler lists them).

---

### Task 1: Add the three seam interfaces and the Levenshtein free function

This task introduces the new types **without removing the old interface yet**, so the solution still builds. The split happens in Task 2.

**Files:**
- Create: `src/PassReset.Common/IPasswordChanger.cs`
- Create: `src/PassReset.Common/IPasswordStatusReader.cs`
- Create: `src/PassReset.Common/IDirectoryUserReader.cs`
- Create: `src/PassReset.Common/PasswordDistance.cs`
- Test: `src/PassReset.Tests/Common/PasswordDistanceTests.cs`

**Interfaces:**
- Consumes: existing types `ApiErrorItem`, `PasswordStatus`, `PasswordPolicy` (all in `PassReset.Common`).
- Produces:
  - `IPasswordChanger.PerformPasswordChangeAsync(string username, string currentPassword, string newPassword) → Task<ApiErrorItem?>`
  - `IPasswordStatusReader.GetUserPasswordStatusAsync(string username, string currentPassword) → Task<PasswordStatus>`
  - `IPasswordStatusReader.GetEffectivePasswordPolicyAsync() → Task<PasswordPolicy?>`
  - `IDirectoryUserReader.GetUserEmail(string username) → string?`
  - `IDirectoryUserReader.GetUsersInGroup(string groupName) → IEnumerable<(string Username, string Email, DateTime? PasswordLastSet)>`
  - `IDirectoryUserReader.GetDomainMaxPasswordAge() → TimeSpan`
  - `PasswordDistance.Levenshtein(string a, string b) → int` (static, in `PassReset.Common`)

- [ ] **Step 1: Write the failing test for the Levenshtein free function**

Create `src/PassReset.Tests/Common/PasswordDistanceTests.cs`:

```csharp
using PassReset.Common;
using Xunit;

namespace PassReset.Tests.Common;

public class PasswordDistanceTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("Passw0rd!", "Passw0rd!", 0)]
    [InlineData("Passw0rd!", "Passw0rd?", 1)]
    public void Levenshtein_MatchesKnownDistances(string a, string b, int expected)
    {
        Assert.Equal(expected, PasswordDistance.Levenshtein(a, b));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/PassReset.sln --filter "FullyQualifiedName~PasswordDistanceTests"`
Expected: FAIL — compile error, `PasswordDistance` does not exist.

- [ ] **Step 3: Create the Levenshtein free function**

Create `src/PassReset.Common/PasswordDistance.cs` (algorithm lifted verbatim from the old interface default method):

```csharp
namespace PassReset.Common;

/// <summary>
/// Levenshtein edit distance between two strings. Used by the change flow to enforce a
/// minimum distance between the old and new password. A deterministic string algorithm —
/// it never varies per directory adapter, so it is a free function, not a provider seam.
/// </summary>
public static class PasswordDistance
{
    /// <summary>Computes the Levenshtein distance between two strings.</summary>
    public static int Levenshtein(string currentPassword, string newPassword)
    {
        var n = currentPassword.Length;
        var m = newPassword.Length;
        var d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (newPassword[j - 1] == currentPassword[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}
```

- [ ] **Step 4: Create the three seam interfaces**

Create `src/PassReset.Common/IPasswordChanger.cs`:

```csharp
namespace PassReset.Common;

/// <summary>
/// The Password Changer seam: the credentialed write path. Authenticates the user with
/// their current password and changes their own password. The only seam wrapped by the
/// lockout and Local Policy decorators.
/// </summary>
public interface IPasswordChanger
{
    /// <summary>
    /// Performs the password change using the credentials provided.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <returns>The API error item, or null if the change succeeded.</returns>
    Task<ApiErrorItem?> PerformPasswordChangeAsync(string username, string currentPassword, string newPassword);
}
```

Create `src/PassReset.Common/IPasswordStatusReader.cs`:

```csharp
namespace PassReset.Common;

/// <summary>
/// The Password Status Reader seam: the credentialed read path serving a Status Check.
/// Authenticates and returns resolved expiry plus the effective AD policy. Read-only.
/// </summary>
public interface IPasswordStatusReader
{
    /// <summary>
    /// Status Check (v2.1): authenticates the user with their current password and, on
    /// success, returns their resolved password expiry plus the effective AD policy. Reuses
    /// the same bind as a Password Change. Never throws — bind failures are returned via
    /// <see cref="PasswordStatus.Error"/> with the precise code; the controller redacts for the wire.
    /// </summary>
    Task<PasswordStatus> GetUserPasswordStatusAsync(string username, string currentPassword);

    /// <summary>
    /// Returns the effective default-domain password policy from RootDSE,
    /// or null if the AD query fails. Implementations must not throw.
    /// </summary>
    Task<PasswordPolicy?> GetEffectivePasswordPolicyAsync();
}
```

Create `src/PassReset.Common/IDirectoryUserReader.cs`:

```csharp
namespace PassReset.Common;

/// <summary>
/// The Directory User Reader seam: the unauthenticated directory-read path used by
/// side-effects (the password-changed email and the expiry-notification background service).
/// Reads directory facts without binding as the user.
/// </summary>
public interface IDirectoryUserReader
{
    /// <summary>
    /// Retrieves the email address for the specified user from the directory.
    /// Returns null if the user is not found or on error.
    /// </summary>
    string? GetUserEmail(string username);

    /// <summary>
    /// Returns user details for all members of the specified AD group (recursive).
    /// Used by the password expiry notification background service.
    /// </summary>
    IEnumerable<(string Username, string Email, DateTime? PasswordLastSet)> GetUsersInGroup(string groupName);

    /// <summary>
    /// Returns the domain maximum password age (maxPwdAge).
    /// Returns TimeSpan.MaxValue if the domain has no password expiry policy.
    /// </summary>
    TimeSpan GetDomainMaxPasswordAge();
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/PassReset.sln --filter "FullyQualifiedName~PasswordDistanceTests"`
Expected: PASS (6 cases). The rest of the solution still builds because `IPasswordChangeProvider` is untouched.

- [ ] **Step 6: Commit**

```bash
git add src/PassReset.Common/IPasswordChanger.cs src/PassReset.Common/IPasswordStatusReader.cs src/PassReset.Common/IDirectoryUserReader.cs src/PassReset.Common/PasswordDistance.cs src/PassReset.Tests/Common/PasswordDistanceTests.cs
git commit -m "refactor(common): add three provider seams + Levenshtein free function"
```

---

### Task 2: Retarget adapters and decorators to the new seams; delete the old interface

This is the mechanical core. After this task the old interface is gone and every adapter/decorator implements the new seams. The solution will not build until every reference is migrated — do them together, then build.

**Files:**
- Modify: `src/PassReset.PasswordProvider/PasswordChangeProvider.cs:16`
- Modify: `src/PassReset.PasswordProvider.Ldap/LdapPasswordChangeProvider.cs:14`
- Modify: `src/PassReset.Web/Helpers/DebugPasswordChangeProvider.cs:11` (+ remove its `MeasureNewPasswordDistance` method)
- Modify: `src/PassReset.PasswordProvider/LockoutPasswordChangeProvider.cs:49,58,135-150`
- Modify: `src/PassReset.Common/LocalPolicy/LocalPolicyPasswordChangeProvider.cs:11,13,54-68`
- Delete: `src/PassReset.Common/IPasswordChangeProvider.cs`

**Interfaces:**
- Consumes: `IPasswordChanger`, `IPasswordStatusReader`, `IDirectoryUserReader` (Task 1).
- Produces: each adapter now satisfies all three seams; each decorator satisfies only `IPasswordChanger` wrapping an inner `IPasswordChanger`.

- [ ] **Step 1: Retarget the three concrete adapters**

In each adapter, change the class declaration to implement the three seams. The method bodies are unchanged.

`src/PassReset.PasswordProvider/PasswordChangeProvider.cs:16`:
```csharp
public sealed class PasswordChangeProvider : IPasswordChanger, IPasswordStatusReader, IDirectoryUserReader
```

`src/PassReset.PasswordProvider.Ldap/LdapPasswordChangeProvider.cs:14`:
```csharp
public sealed class LdapPasswordChangeProvider : IPasswordChanger, IPasswordStatusReader, IDirectoryUserReader
```

`src/PassReset.Web/Helpers/DebugPasswordChangeProvider.cs:11`:
```csharp
internal sealed class DebugPasswordChangeProvider : IPasswordChanger, IPasswordStatusReader, IDirectoryUserReader
```

- [ ] **Step 2: Remove `MeasureNewPasswordDistance` from `DebugPasswordChangeProvider`**

Delete the method that forwards `MeasureNewPasswordDistance` (it forwarded to the old default; the algorithm now lives in `PasswordDistance`). If `PasswordChangeProvider` or `LdapPasswordChangeProvider` declare an override of `MeasureNewPasswordDistance`, delete those too. (The Windows/LDAP adapters relied on the interface default, so they likely have nothing to remove — verify with a grep in Step 5.)

- [ ] **Step 3: Slim the Lockout decorator to `IPasswordChanger` only**

`src/PassReset.PasswordProvider/LockoutPasswordChangeProvider.cs`:

Line 49 — change the declaration:
```csharp
public sealed class LockoutPasswordChangeProvider : IPasswordChanger, ILockoutDiagnostics, IDisposable
```

Line 58 — change the inner field type:
```csharp
    private readonly IPasswordChanger _inner;
```

Line 64 — change the constructor parameter type:
```csharp
        IPasswordChanger inner,
```

Delete the five forwarding methods at lines 135–150 (`GetUserEmail`, `GetUsersInGroup`, `GetDomainMaxPasswordAge`, `GetUserPasswordStatusAsync`, `GetEffectivePasswordPolicyAsync`). Keep `PerformPasswordChangeAsync` (lines 83–132) and all private helpers untouched.

- [ ] **Step 4: Slim the LocalPolicy decorator to `IPasswordChanger` only**

`src/PassReset.Common/LocalPolicy/LocalPolicyPasswordChangeProvider.cs`:

Line 11 — change the declaration:
```csharp
public sealed class LocalPolicyPasswordChangeProvider : IPasswordChanger
```

Line 13 — change the inner field type:
```csharp
    private readonly IPasswordChanger _inner;
```

Change the constructor parameter that accepts the inner provider to `IPasswordChanger inner`.

Delete the six forwarding methods at lines 54–68 (`GetUserEmail`, `GetUsersInGroup`, `GetDomainMaxPasswordAge`, `GetUserPasswordStatusAsync`, `GetEffectivePasswordPolicyAsync`, `MeasureNewPasswordDistance`). Keep `PerformPasswordChangeAsync` (lines 30–52) and its private helpers.

- [ ] **Step 5: Delete the old interface and find every remaining reference**

```bash
rm src/PassReset.Common/IPasswordChangeProvider.cs
grep -rn "IPasswordChangeProvider\|MeasureNewPasswordDistance" --include="*.cs" src
```
Expected: remaining hits are in consumers (Task 3) and tests (Task 4). Note them — production-code adapters/decorators should show ZERO hits now.

- [ ] **Step 6: Build to confirm the provider + common projects compile**

Run: `dotnet build src/PassReset.PasswordProvider/PassReset.PasswordProvider.csproj -c Release`
Run: `dotnet build src/PassReset.Common/PassReset.Common.csproj -c Release`
Expected: both succeed. (The Web project will still fail until Task 3 — that is expected.)

- [ ] **Step 7: Commit**

```bash
git add src/PassReset.PasswordProvider src/PassReset.PasswordProvider.Ldap src/PassReset.Web/Helpers/DebugPasswordChangeProvider.cs src/PassReset.Common/LocalPolicy/LocalPolicyPasswordChangeProvider.cs
git rm src/PassReset.Common/IPasswordChangeProvider.cs
git commit -m "refactor(provider): adapters implement 3 seams, decorators wrap IPasswordChanger only"
```

---

### Task 3: Retarget consumers and DI wiring

Migrate the three production consumers and the DI container so the Web project builds and runs identically.

**Files:**
- Modify: `src/PassReset.PasswordProvider/PasswordPolicyCache.cs:18,20`
- Modify: `src/PassReset.Web/Controllers/PasswordController.cs:22,39,156,187,211,259`
- Modify: `src/PassReset.Web/Services/PasswordExpiryNotificationService.cs:86,180`
- Modify: `src/PassReset.Web/Program.cs:286-408`

**Interfaces:**
- Consumes: the three seams; `PasswordDistance.Levenshtein`.
- Produces: a Web app whose DI exposes `IPasswordChanger` (decorated), `IPasswordStatusReader` (undecorated adapter), `IDirectoryUserReader` (undecorated adapter), all backed by one adapter instance.

- [ ] **Step 1: Retarget `PasswordPolicyCache` to `IPasswordStatusReader`**

`src/PassReset.PasswordProvider/PasswordPolicyCache.cs`:

Line 18:
```csharp
    private readonly IPasswordStatusReader _provider;
```
Line 20 (constructor parameter):
```csharp
    public PasswordPolicyCache(IMemoryCache cache, IPasswordStatusReader provider)
```
The `GetEffectivePasswordPolicyAsync()` call body at line 31 is unchanged (the method now lives on `IPasswordStatusReader`). Update the `<see cref>` in the XML doc from `IPasswordChangeProvider.GetEffectivePasswordPolicyAsync` to `IPasswordStatusReader.GetEffectivePasswordPolicyAsync`.

- [ ] **Step 2: Retarget `PasswordController` injection and the Levenshtein call**

`src/PassReset.Web/Controllers/PasswordController.cs`:

Replace the single provider field (line 22) with three:
```csharp
    private readonly IPasswordChanger _changer;
    private readonly IPasswordStatusReader _statusReader;
    private readonly IDirectoryUserReader _directoryReader;
```

Replace the constructor parameter (line 39) and assignments (line 51):
```csharp
        IPasswordChanger changer,
        IPasswordStatusReader statusReader,
        IDirectoryUserReader directoryReader,
```
```csharp
        _changer         = changer;
        _statusReader    = statusReader;
        _directoryReader = directoryReader;
```

Update the four call sites:
- Line 156 (distance check) → free function:
  ```csharp
        if (settings.MinimumDistance > 0 &&
            PasswordDistance.Levenshtein(model.CurrentPassword, model.NewPassword) < settings.MinimumDistance)
  ```
- Line 187 (change) → `await _changer.PerformPasswordChangeAsync(model.Username, model.CurrentPassword, model.NewPassword);`
- Line 211 (email lookup) → `var emailAddress = _directoryReader.GetUserEmail(username);`
- Line 259 (status) → `var status = await _statusReader.GetUserPasswordStatusAsync(model.Username, model.CurrentPassword);`

- [ ] **Step 3: Retarget `PasswordExpiryNotificationService` to `IDirectoryUserReader`**

`src/PassReset.Web/Services/PasswordExpiryNotificationService.cs`:

Line 86:
```csharp
            var provider       = scope.ServiceProvider.GetRequiredService<IDirectoryUserReader>();
```
Line 180 (helper signature):
```csharp
        GetGroupUsersThrottledAsync(IDirectoryUserReader provider, string groupName, CancellationToken ct)
```
Update the `<see cref>` on line 176 to `IDirectoryUserReader.GetUsersInGroup`. The `GetDomainMaxPasswordAge()` (line 91) and `GetUsersInGroup()` (line 185) call bodies are unchanged.

- [ ] **Step 4: Rewire DI — register the adapter once, map three seams, decorate only the changer**

`src/PassReset.Web/Program.cs`. In each of the three branches (Debug 286–308, Ldap 309–355, Windows 356–384), the concrete adapter is already registered as a singleton (`DebugPasswordChangeProvider` / `LdapPasswordChangeProvider` / `PasswordChangeProvider`). Keep those registrations. Change the `LockoutPasswordChangeProvider` registration in each branch so its inner type is resolved as `IPasswordChanger` from the concrete singleton — the existing `sp.GetRequiredService<TConcrete>()` already returns the right instance, and `TConcrete` now implements `IPasswordChanger`, so these blocks need no change beyond confirming they compile.

Replace the shared chain block (lines 398–408) with the three seam mappings:

```csharp
    // Change seam: LocalPolicy( Lockout( adapter ) ) — only the credentialed write path is decorated.
    builder.Services.AddSingleton<IPasswordChanger>(sp =>
    {
        var lockout = sp.GetRequiredService<LockoutPasswordChangeProvider>();
        var banned  = sp.GetRequiredService<PassReset.Common.LocalPolicy.BannedWordsChecker>();
        var pwned   = sp.GetRequiredService<PassReset.Common.LocalPolicy.LocalPwnedPasswordsChecker>();
        var log     = sp.GetRequiredService<ILogger<PassReset.Common.LocalPolicy.LocalPolicyPasswordChangeProvider>>();
        return new PassReset.Common.LocalPolicy.LocalPolicyPasswordChangeProvider(lockout, banned, pwned, log);
    });

    // Status + Directory seams: resolve straight to the single adapter instance, undecorated.
    // The concrete type is branch-specific; map both seams to whichever adapter was registered above.
    builder.Services.AddSingleton<IPasswordStatusReader>(ResolveAdapter);
    builder.Services.AddSingleton<IDirectoryUserReader>(sp => (IDirectoryUserReader)ResolveAdapter(sp));

    builder.Services.AddSingleton<ILockoutDiagnostics>(sp =>
        sp.GetRequiredService<LockoutPasswordChangeProvider>());
```

Add a local helper near the registration block that returns the single adapter instance as `IPasswordStatusReader`, branching on the same `webSettings.UseDebugProvider` / `effectiveProvider` decision already computed above:

```csharp
    IPasswordStatusReader ResolveAdapter(IServiceProvider sp)
    {
        if (webSettings.UseDebugProvider)
            return sp.GetRequiredService<DebugPasswordChangeProvider>();
        if (effectiveProvider == WiringTarget.Ldap)
            return sp.GetRequiredService<LdapPasswordChangeProvider>();
#if WINDOWS_PROVIDER
        return sp.GetRequiredService<PasswordChangeProvider>();
#else
        throw new InvalidOperationException(
            "ProviderMode resolved to Windows, but this build excludes the Windows provider.");
#endif
    }
```

> Note: `DebugPasswordChangeProvider` is `internal` in `PassReset.Web` — `Program.cs` is in the same assembly, so the cast/resolve is legal. `ResolveAdapter` returns the same singleton instance for both the status and directory seams, satisfying the one-instance constraint.

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build src/PassReset.sln -c Release`
Expected: SUCCESS, zero errors. Remaining `IPasswordChangeProvider` references (if any) are only in test code (Task 4).

- [ ] **Step 6: Commit**

```bash
git add src/PassReset.PasswordProvider/PasswordPolicyCache.cs src/PassReset.Web/Controllers/PasswordController.cs src/PassReset.Web/Services/PasswordExpiryNotificationService.cs src/PassReset.Web/Program.cs
git commit -m "refactor(web): inject 3 seams into consumers, rewire DI to share one adapter"
```

---

### Task 4: Retarget tests and prove behavior is preserved

Migrate test fakes and the contract base off the old interface, then run the full suite — the proof that the carve changed nothing.

**Files:**
- Modify: `src/PassReset.Tests/Contracts/IPasswordChangeProviderContract.cs` (rename to `IPasswordChangerContract.cs`)
- Modify: any test fakes / provider test files that declare `: IPasswordChangeProvider` or call `MeasureNewPasswordDistance` (enumerated by the grep below)

**Interfaces:**
- Consumes: `IPasswordChanger` (and the other two seams where a fake needs them), `PasswordDistance.Levenshtein`.
- Produces: a green test suite.

- [ ] **Step 1: Enumerate the remaining test references**

```bash
grep -rn "IPasswordChangeProvider\|MeasureNewPasswordDistance" --include="*.cs" src
```
Expected: only test-project hits remain. For each:
- A fake that implements the full old interface → implement the three seams (`IPasswordChanger, IPasswordStatusReader, IDirectoryUserReader`). The method bodies are unchanged; only the declaration splits.
- A test calling `provider.MeasureNewPasswordDistance(...)` → replace with `PasswordDistance.Levenshtein(...)`.

- [ ] **Step 2: Retarget the contract base**

In `src/PassReset.Tests/Contracts/IPasswordChangeProviderContract.cs`:
- Rename the class (and file) to `IPasswordChangerContract`.
- Change the two abstract factory return types:
  ```csharp
      protected abstract IPasswordChanger CreateProvider();
      ...
      protected abstract IPasswordChanger SeedBannedWord(string term);
  ```
- The `Sut` reference in the `SeedBannedWord` doc comment (line 42) and all `PerformPasswordChangeAsync` call sites are unchanged — that method is on `IPasswordChanger`.
- Update any subclasses (the Windows and LDAP contract test fixtures) that override `CreateProvider`/`SeedBannedWord` to return `IPasswordChanger`. Find them:
  ```bash
  grep -rn ": IPasswordChangeProviderContract\|: IPasswordChangerContract" --include="*.cs" src
  ```

- [ ] **Step 3: Run the contract tests**

Run: `dotnet test src/PassReset.sln --filter "FullyQualifiedName~Contract"`
Expected: PASS — same assertions as before; the change seam behavior is untouched.

- [ ] **Step 4: Run the full suite (backend)**

Run: `dotnet test src/PassReset.sln -c Release`
Expected: PASS, no skipped-due-to-compile, same test count as before the refactor (minus none, plus the 6 new `PasswordDistanceTests` cases).

- [ ] **Step 5: Run the frontend suite (unaffected, but the contract says verify)**

Run: `cd src/PassReset.Web/ClientApp && npm test`
Expected: PASS. (No frontend files changed; this confirms the build artifact wiring is intact.)

- [ ] **Step 6: Final full build + commit**

```bash
dotnet build src/PassReset.sln -c Release
git add src/PassReset.Tests
git commit -m "refactor(test): retarget fakes + contract base to IPasswordChanger; full suite green"
```

---

## Self-Review

**Spec coverage** (against the grilling decisions):
- Q1 three seams → Tasks 1–3 create/wire `IPasswordChanger`, `IPasswordStatusReader`, `IDirectoryUserReader`. ✓
- Q2 Levenshtein → free function → Task 1 `PasswordDistance`, Task 3 Step 2 call-site swap, Task 2 Step 2 removes adapter copies. ✓
- Q3 decorators wrap only `IPasswordChanger` → Task 2 Steps 3–4. ✓
- Q4 one adapter instance, status/directory undecorated → Task 3 Step 4 `ResolveAdapter` shared singleton. ✓
- Q5 DI maps concrete-once to three seams, ProviderMode branching preserved → Task 3 Step 4. ✓
- Q6 delete old interface, no composed marker → Task 2 Step 5. ✓
- Q7 verify via existing suite + build inline → Task 4. ✓
- CONTEXT.md "Provider Seams" terms → reflected in interface XML docs and Global Constraints. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code; commands have expected output. ✓

**Type consistency:** `IPasswordChanger.PerformPasswordChangeAsync`, `IPasswordStatusReader.{GetUserPasswordStatusAsync,GetEffectivePasswordPolicyAsync}`, `IDirectoryUserReader.{GetUserEmail,GetUsersInGroup,GetDomainMaxPasswordAge}`, `PasswordDistance.Levenshtein` — names identical across Tasks 1→2→3→4. ✓

**Known caveat surfaced for the implementer:** the Status Check path is *not* fed into portal lockout today (the Lockout decorator only wraps the change seam, and that is unchanged here). This refactor preserves that behavior exactly — do not "fix" it as part of this task.
