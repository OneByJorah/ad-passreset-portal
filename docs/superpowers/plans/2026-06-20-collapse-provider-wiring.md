# Collapse the Three-Way Provider Wiring — Implementation Plan

> **For agentic workers:** single mechanical task, behavior-preserving. Execute inline with verification against the integration suites (this file has no unit tests by design). Steps use checkbox (`- [ ]`) syntax.

**Goal:** Collapse the repeated Debug/Ldap/Windows DI registration blocks in `PassReset.Web/Program.cs` so the shared wiring (lockout decorator, seam mapping, expiry service) is registered ONCE, and remove the unsafe `(IDirectoryUserReader)ResolveAdapter(sp)` downcast — without changing any runtime behavior.

**Architecture:** Each provider branch records only what genuinely differs (its concrete adapter type via `Type adapterType`, plus its email service, AD-probe, and any branch-specific factory). After the branches, the lockout decorator, the `LocalPolicy(Lockout(adapter))` change seam, the status/directory seam mapping (via centralized casts off `adapterType`), lockout diagnostics, and the expiry-or-null service are registered once. `WiringTarget`/`ProviderMode` selection and the `#if WINDOWS_PROVIDER` guard are unchanged.

**Tech Stack:** C# 13, .NET 10, ASP.NET Core DI. `Program.cs` only.

## Global Constraints

- **Behavior preservation is the prime directive.** No registration's runtime effect may change. The gate is the full Windows integration suite (`WebApplicationFactory<Program>` boots the real DI graph) + the cross-platform suite (exercises the LDAP/Debug provider paths). Both must pass unchanged.
- **Both build configurations must compile:** the default Windows build (`WINDOWS_PROVIDER` defined) AND the non-Windows build (`#else` arm). The Windows arm and its `IPrincipalContextFactory` registration stay inside `#if WINDOWS_PROVIDER`.
- **Preserve the decorator decision (CONTEXT.md):** only the Change seam (`IPasswordChanger`) is decorated as `LocalPolicy(Lockout(adapter))`. Status and Directory seams resolve to the bare adapter. Do not decorate them.
- **Preserve per-branch differences exactly:** Debug → `NoOpEmailService` + `LdapTcpProbe`; Ldap → `SmtpEmailService` (transient) + `LdapTcpProbe` + the `Func<ILdapSession>` factory; Windows → `SmtpEmailService` (transient) + `DomainJoinedProbe` + `IPrincipalContextFactory`.
- **`LockoutPasswordChangeProvider` stays a singleton** also resolved by `ILockoutDiagnostics` — same instance. `IEmailService` lifetimes stay as today (NoOp singleton; Smtp transient).
- **Scope:** `Program.cs` only. No changes to providers, decorators, or tests. No new files.
- **Commit convention:** `refactor(web): <subject>`. End commit body with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- **Verify commands:**
  - Windows build + tests: `dotnet build src/PassReset.sln -c Release` then `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj -c Release` and `dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release`
  - Non-Windows arm compiles: build `PassReset.Web` with `WINDOWS_PROVIDER` undefined (see Task step 6).

---

## File Structure

- Modify: `src/PassReset.Web/Program.cs` — the provider-registration region (currently ~lines 286–428): the three `if/else if/#if-else` branches, the once-registered seam block (398–414), and the `ResolveAdapter` local function (416–428).

Variables already in scope before the region: `webSettings` (223), `expirySettings` (227), `passwordChangeOptions` (241), `effectiveProvider` (277). `WiringTarget { Windows, Ldap }` is defined at the file end (670).

---

### Task 1: Collapse the provider-wiring region

**Files:**
- Modify: `src/PassReset.Web/Program.cs` (provider-registration region, ~286–428)

**Interfaces:**
- Consumes (existing): `DebugPasswordChangeProvider`, `LdapPasswordChangeProvider`, `PasswordChangeProvider` (all implement `IPasswordChanger`, `IPasswordStatusReader`, `IDirectoryUserReader`); `LockoutPasswordChangeProvider(IPasswordChanger inner, IOptions<PasswordChangeOptions>, ILogger<>)`; `LocalPolicyPasswordChangeProvider(IPasswordChanger inner, BannedWordsChecker, LocalPwnedPasswordsChecker, ILogger<>)`; `NoOpEmailService`, `SmtpEmailService`, `LdapTcpProbe`, `DomainJoinedProbe`, `DefaultPrincipalContextFactory`, `PasswordExpiryNotificationService`, `NullExpiryServiceDiagnostics`.
- Produces: no new public surface — this is an internal reorganization of `Program.cs`.

**The target shape.** Replace the region (the three branches at 286–392, the once-block at 394–414, and `ResolveAdapter` at 416–428) with: per-branch blocks that set a `Type adapterType` and register only branch-specific services, followed by a single shared block. Local helper functions (`RegisterExpiry`) live at the end of the region or as local functions.

- [ ] **Step 1: Read the current region to anchor the edit**

Read `src/PassReset.Web/Program.cs` lines 285–428 so the exact surrounding text (the `if (webSettings.UseDebugProvider)` opener and the lines after `ResolveAdapter`'s closing brace) is known for an exact-match replacement.

- [ ] **Step 2: Replace the three branches (286–392) with branch blocks that set `adapterType` + branch-specific services only**

The new branch region (replacing lines 286 through the `#endif` at 392). `Type adapterType` is declared before the `if`:

```csharp
    // Phase 11 + Candidate 3: each branch records only what DIFFERS — its concrete adapter
    // type and its branch-specific companions (email, AD probe, session/context factory).
    // The shared wiring (lockout decorator, seam mapping, expiry) is registered ONCE below.
    Type adapterType;

    if (webSettings.UseDebugProvider)
    {
        builder.Services.AddSingleton<DebugPasswordChangeProvider>();
        adapterType = typeof(DebugPasswordChangeProvider);
        builder.Services.AddSingleton<IEmailService, NoOpEmailService>();
        // Health probe — LDAP TCP probe is cross-platform; returns NotConfigured when LdapHostnames empty.
        builder.Services.AddSingleton<IAdConnectivityProbe, LdapTcpProbe>();
    }
    else if (effectiveProvider == WiringTarget.Ldap)
    {
        // Session factory per password-change request (no pooling — low frequency).
        builder.Services.AddSingleton<Func<ILdapSession>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PasswordChangeOptions>>().Value;
            // Belt-and-suspenders: PasswordChangeOptionsValidator already enforces non-empty
            // LdapHostnames when ProviderMode resolves to Ldap at startup. This defensive
            // check surfaces a clear, actionable error if the options are ever reloaded
            // into an invalid state before the factory fires.
            if (opts.LdapHostnames is null || opts.LdapHostnames.Length == 0)
            {
                throw new InvalidOperationException(
                    "PasswordChangeOptions.LdapHostnames must contain at least one hostname when ProviderMode=Ldap.");
            }
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return () => new LdapSession(
                hostname: opts.LdapHostnames[0],
                port: opts.LdapPort,
                useLdaps: opts.LdapUseSsl,
                serviceAccountDn: opts.ServiceAccountDn,
                serviceAccountPassword: opts.ServiceAccountPassword,
                trustedThumbprints: opts.LdapTrustedCertificateThumbprints,
                logger: loggerFactory.CreateLogger<LdapSession>());
        });
        builder.Services.AddSingleton<LdapPasswordChangeProvider>();
        adapterType = typeof(LdapPasswordChangeProvider);
        builder.Services.AddTransient<IEmailService, SmtpEmailService>();
        // Health probe — cross-platform LDAP TCP probe.
        builder.Services.AddSingleton<IAdConnectivityProbe, LdapTcpProbe>();
    }
#if WINDOWS_PROVIDER
    else  // effectiveProvider == WiringTarget.Windows
    {
        builder.Services.AddSingleton<PassReset.PasswordProvider.IPrincipalContextFactory,
                                      PassReset.PasswordProvider.DefaultPrincipalContextFactory>();
        builder.Services.AddSingleton<PasswordChangeProvider>();
        adapterType = typeof(PasswordChangeProvider);
        builder.Services.AddTransient<IEmailService, SmtpEmailService>();
        // Health probe — Windows domain-joined PrincipalContext check.
        builder.Services.AddSingleton<IAdConnectivityProbe, PassReset.PasswordProvider.DomainJoinedProbe>();
    }
#else
    else
    {
        throw new InvalidOperationException(
            "PasswordChangeOptions.ProviderMode resolved to Windows, but this build does not include the Windows provider. " +
            "Rebuild on Windows or set ProviderMode to Ldap.");
    }
#endif
```

Note: the `throw` in the `#else` arm means `adapterType` is definitely assigned on every reachable path the compiler sees (Debug assigns, Ldap assigns, Windows-arm assigns, non-Windows-else throws). If the compiler complains about unassigned `adapterType` in the non-Windows build, the `throw` covers it — confirm in Step 6.

- [ ] **Step 3: Replace the once-block (394–414) + `ResolveAdapter` (416–428) with the single shared block**

After the branch region, the existing LocalPolicy checker registrations (394–396) stay. Replace from the `IPasswordChanger` registration (398) through the end of `ResolveAdapter` (428) with:

```csharp
    // ─── LocalPolicy checkers (Phase 12) ─────────────────────────────────────────
    builder.Services.AddSingleton<PassReset.Common.LocalPolicy.BannedWordsChecker>();
    builder.Services.AddSingleton<PassReset.Common.LocalPolicy.LocalPwnedPasswordsChecker>();

    // ─── Shared provider wiring (registered ONCE, independent of the branch above) ──
    // Lockout decorator wraps whichever concrete adapter the branch selected.
    builder.Services.AddSingleton<LockoutPasswordChangeProvider>(sp =>
        new LockoutPasswordChangeProvider(
            (IPasswordChanger)sp.GetRequiredService(adapterType),
            sp.GetRequiredService<IOptions<PasswordChangeOptions>>(),
            sp.GetRequiredService<ILogger<LockoutPasswordChangeProvider>>()));

    // Change seam: LocalPolicy( Lockout( adapter ) ) — only the credentialed write path is decorated.
    builder.Services.AddSingleton<IPasswordChanger>(sp =>
    {
        var lockout = sp.GetRequiredService<LockoutPasswordChangeProvider>();
        var banned  = sp.GetRequiredService<PassReset.Common.LocalPolicy.BannedWordsChecker>();
        var pwned   = sp.GetRequiredService<PassReset.Common.LocalPolicy.LocalPwnedPasswordsChecker>();
        var log     = sp.GetRequiredService<ILogger<PassReset.Common.LocalPolicy.LocalPolicyPasswordChangeProvider>>();
        return new PassReset.Common.LocalPolicy.LocalPolicyPasswordChangeProvider(lockout, banned, pwned, log);
    });

    // Status + Directory seams: the selected adapter implements both. One centralized cast each
    // off `adapterType` — replaces the former cross-seam downcast on the resolver's return value.
    builder.Services.AddSingleton<IPasswordStatusReader>(sp =>
        (IPasswordStatusReader)sp.GetRequiredService(adapterType));
    builder.Services.AddSingleton<IDirectoryUserReader>(sp =>
        (IDirectoryUserReader)sp.GetRequiredService(adapterType));

    builder.Services.AddSingleton<ILockoutDiagnostics>(sp =>
        sp.GetRequiredService<LockoutPasswordChangeProvider>());

    // Expiry notification service — identical in every branch; depends only on the toggle.
    RegisterExpiry(builder.Services, expirySettings.Enabled);

    static void RegisterExpiry(IServiceCollection services, bool enabled)
    {
        if (enabled)
        {
            // Register as singleton so both the hosted service runtime and the health
            // controller's IExpiryServiceDiagnostics dependency resolve the SAME instance.
            services.AddSingleton<PasswordExpiryNotificationService>();
            services.AddHostedService(sp => sp.GetRequiredService<PasswordExpiryNotificationService>());
            services.AddSingleton<IExpiryServiceDiagnostics>(sp =>
                sp.GetRequiredService<PasswordExpiryNotificationService>());
        }
        else
        {
            services.AddSingleton<IExpiryServiceDiagnostics>(new NullExpiryServiceDiagnostics());
        }
    }
```

Note: `RegisterExpiry` is a local function — it must appear after all top-level statements in its enclosing scope per C# rules, or be placed where other local functions in `Program.cs` live. If the compiler rejects its position, move it to sit beside `ResolveAdapter`'s former location (it's being deleted) or convert to a `static` local function at the end of the registration block. Confirm placement compiles in Step 5.

- [ ] **Step 4: Verify no orphaned references to the deleted `ResolveAdapter`**

Search `Program.cs` for `ResolveAdapter` — there must be zero remaining references (it was only used by the two seam registrations, now replaced). Run:

`grep -n "ResolveAdapter" src/PassReset.Web/Program.cs` — expect no output.

- [ ] **Step 5: Build the default (Windows) configuration**

Run: `dotnet build src/PassReset.sln -c Release`
Expected: Build succeeds, 0 errors. (Pre-existing warnings unrelated to this change are acceptable.) If `adapterType` is reported as possibly-unassigned, verify the `#else` arm's `throw` is intact; if `RegisterExpiry` placement errors, relocate it per Step 3's note.

- [ ] **Step 6: Verify the non-Windows arm compiles**

The `#else` arm (the `throw`) is only compiled when `WINDOWS_PROVIDER` is undefined. Confirm it compiles by building the Web project with the symbol undefined:

Run: `dotnet build src/PassReset.Web/PassReset.Web.csproj -c Release -p:DefineConstants=""`
Expected: the `#else` branch compiles (the Windows-typed registrations are excluded). If the project's csproj force-defines `WINDOWS_PROVIDER` such that this override is ignored, instead visually confirm: in the `#else` arm `adapterType` is never assigned but the arm throws, so definite-assignment holds; and the shared block references no Windows types. Document which check was used in the report.

- [ ] **Step 7: Run the integration suites — the behavior gate**

Run: `dotnet test src/PassReset.Tests.Windows/PassReset.Tests.Windows.csproj -c Release`
Expected: PASS — all tests, including every `GenericErrorMappingTests` (these boot the full DI graph through `WebApplicationFactory<Program>`, proving the rewired registrations resolve and behave identically) and the Debug-provider-swap tests.

Run: `dotnet test src/PassReset.Tests/PassReset.Tests.csproj -c Release`
Expected: PASS — exercises the LDAP provider path.

If ANY test fails, STOP — the rewiring changed behavior. Diagnose before committing.

- [ ] **Step 8: Commit**

```bash
git add src/PassReset.Web/Program.cs docs/superpowers/plans/2026-06-20-collapse-provider-wiring.md
git commit -m "refactor(web): collapse three-way provider wiring; drop unsafe downcast

Each provider branch now records only its concrete adapter type + branch-specific
services (email, AD probe, session/context factory). The lockout decorator, change
seam, status/directory seam mapping, lockout diagnostics, and expiry service are
registered once. The cross-seam (IDirectoryUserReader) downcast is replaced by two
centralized casts off the selected adapter type. Behavior-preserving.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

- **Spec coverage:** branches set `adapterType` + branch-specifics (Step 2); shared block built once (Step 3); downcast removed (Step 3 — two explicit casts); expiry extracted (Step 3, `RegisterExpiry`); both build configs (Steps 5–6); behavior gate (Step 7). All design points covered.
- **Behavior equivalence reasoning:** every registration that existed still exists with the same lifetime and same resolved instance — `LockoutPasswordChangeProvider` still wraps the same concrete adapter (now via `adapterType` instead of a typed `GetRequiredService<T>()`, resolving the identical singleton); `IPasswordChanger`/status/directory/diagnostics/email/probe/expiry all unchanged in effect. The only deletions are the two duplicate copies of the lockout factory and expiry block, and the `ResolveAdapter` function (its two call sites are replaced by equivalent casts).
- **Placeholder scan:** none — full code in every step.
- **Risk:** `RegisterExpiry` local-function placement and `adapterType` definite-assignment under the non-Windows build are the two compiler edge cases; both have explicit fallback notes and are caught by Steps 5–6.
