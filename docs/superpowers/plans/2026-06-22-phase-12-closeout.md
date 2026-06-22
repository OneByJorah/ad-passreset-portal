# Phase 12 — Local Password DB: Closeout Plan

> **Supersedes** `docs/superpowers/plans/2026-04-21-phase-12-local-password-db.md`, which is
> **obsolete**. That plan was written against the pre-refactor architecture
> (`IPasswordChangeProvider`, appsettings-only config, `AddScoped` provider chain). Three
> refactors landed afterward — *carve password-provider seams* (2026-06-19), *deepen change
> flow* (2026-06-20), and *collapse provider wiring* (2026-06-20) — and a prior session then
> implemented all of Phase 12 against the **new** architecture. The implementation is complete,
> committed, wired, documented, and tested; only working-tree bookkeeping remains.

**Goal:** Reconcile the working tree and ledgers with the already-shipped Phase 12 feature. No
feature code changes.

**Verification baseline (run 2026-06-22, inline per project lesson):**
- `dotnet build src/PassReset.sln -c Release` → **0 warnings / 0 errors**
- `PassReset.Tests` (cross-platform, net10.0) → **151 passed / 0 failed / 0 skipped**
- `PassReset.Tests.Windows` (behavior gate) → **253 passed / 5 skipped / 0 failed**

---

## Reality map — what changed vs. the 2026-04-21 plan

| Plan assumption | Current reality | Evidence |
|---|---|---|
| Seam = `IPasswordChangeProvider` | Renamed to **`IPasswordChanger`** | `src/PassReset.Common/IPasswordChanger.cs` |
| Decorator implements `IPasswordChangeProvider` | Implements `IPasswordChanger` | `LocalPolicyPasswordChangeProvider.cs:11` |
| Config is appsettings-only (Task 13) | Also editable via **Admin UI** (`IAppSettingsEditor`, `LocalPolicySection`) | `src/PassReset.Web/Areas/Admin/Pages/LocalPolicy.cshtml[.cs]` |
| DI: `AddScoped<IPasswordChangeProvider>(Lockout(core))` | `AddSingleton<IPasswordChanger>` → `LocalPolicy(Lockout(core))`; HIBP `disabled` flag wired | `Program.cs:148-149, 351-369` |
| Task 11 edits `IPasswordChangeProviderContract.cs` | Renamed → `IPasswordChangerContract.cs`; `LocalBannedWord` fact + `SeedBannedWord` preserved | `IPasswordChangerContract.cs:45, 141` |

---

## Task status (original 16 tasks, against current tree)

| # | Original task | Status |
|---|---|---|
| 1 | `ApiErrorCode` 20/21 + FE mirror | ✅ Done — `ApiErrorCode.cs:79-86`, `settings.ts:170-171` |
| 2 | `LocalPolicyOptions` | ✅ Done — `LocalPolicy/LocalPolicyOptions.cs`, wired in `PasswordChangeOptions.cs:192` |
| 3–4 | `BannedWordsChecker` | ✅ Done — impl + tests green |
| 5–6 | `LocalPwnedPasswordsChecker` | ✅ Done — impl + tests green |
| 7–8 | `LocalPolicyPasswordChangeProvider` | ✅ Done — decorates `IPasswordChanger` |
| 9 | `PwnedPasswordChecker.disabled` | ✅ Done — `PwnedPasswordChecker.cs:35`; wired `Program.cs:148-149` |
| 10 | Validator fail-fast | ✅ Done — `PasswordChangeOptionsValidator.cs:66-104` |
| 11 | Contract-test extension | ✅ Done — ported to `IPasswordChangerContract.cs` |
| 12 | `Program.cs` DI wiring | ✅ Done — `Program.cs:351-369` |
| 13 | appsettings defaults | ✅ Done — `appsettings.json:73`, `appsettings.Production.template.json:62` |
| 14 | Operator docs | ✅ Done — `docs/LocalPasswordPolicy-Setup.md`, `appsettings-Production.md:238`, README:43, CLAUDE.md |
| 15 | CHANGELOG | ✅ Done — `CHANGELOG.md` (v2.0 release section bundles it) |
| 16 | Regression check | ✅ Done — see baseline above |

**No feature work remains.**

---

## Outstanding: working-tree bookkeeping only

### Task C1: Stage the contract-file rename

The old `IPasswordChangeProviderContract.cs` shows as `D` (deleted) in the working tree. It was
renamed to `IPasswordChangerContract.cs` (already tracked) during the seam refactor; the deletion
is the trailing half git hasn't been told to pair. The `LocalBannedWord` fact and `SeedBannedWord`
helper are confirmed present in the new file.

- [ ] **Step 1: Confirm orphaned (no references)**

  Run: `grep -rn "IPasswordChangeProviderContract" src/`
  Expected: no matches (verified 2026-06-22).

- [ ] **Step 2: Stage the deletion**

  ```bash
  git add src/PassReset.Tests/Contracts/IPasswordChangeProviderContract.cs
  ```

### Task C2: Update the SDD progress ledger

`.superpowers/sdd/progress.md` still describes the *Deepen Change Flow* phase. Append a
Phase 12 closeout note (or replace, at the author's discretion) recording that Phase 12 shipped
against the post-refactor architecture, with the verification baseline above.

- [ ] **Step 1: Append the Phase 12 entry** (feature complete; tasks 1–16 ✅; baseline 0/0, 151/0/0, 253/5/0).

### Task C3: Supersede the stale plan + spec

The 2026-04-21 plan and spec are untracked and obsolete.

- [ ] **Step 1:** This closeout plan carries the `> Supersedes` banner. Either delete the
  2026-04-21 plan/spec, or commit them with a one-line "superseded by 2026-06-22-closeout"
  header. Author's call — keep for provenance vs. remove to avoid confusion.

### Task C4 (optional): `skills-lock.json`

`?? skills-lock.json` is an unrelated tooling artifact, not part of Phase 12. Decide separately
whether to track or gitignore it. **Out of scope for this closeout.**

### Task C5: Commit the bookkeeping

- [ ] **Step 1: Single bookkeeping commit**

  ```bash
  git add src/PassReset.Tests/Contracts/IPasswordChangeProviderContract.cs \
          .superpowers/sdd/progress.md \
          docs/superpowers/plans/2026-06-22-phase-12-closeout.md
  git commit -m "chore: close out phase 12 (local password DB); record rename + ledger [phase-12]"
  ```

- [ ] **Step 2: Re-verify clean tree**

  Run: `git status --short`
  Expected: only intentional remainders (`skills-lock.json`, and the 2026-04-21 plan/spec if kept).

---

## Self-review

- **No feature code touched** — this plan only stages a rename, updates ledgers, and supersedes
  a stale plan. Behavior is unchanged; the green baseline already proves the feature works.
- **Contract parity preserved** — the `LocalBannedWord` fact survived the rename; no test lost.
- **Risk:** none to runtime. The only risk is leaving the working tree half-staged; Task C5 Step 2
  guards against it.
