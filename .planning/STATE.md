---
state_version: 1.0
milestone: v2.0
milestone_name: Platform evolution
status: shipped
last_updated: "2026-06-01T07:10:00.000Z"
progress:
  total_phases: 8
  completed_phases: 8
  total_plans: 43
  completed_plans: 43
  percent: 100
---

# PassReset — Project State

**Last updated:** 2026-06-01

## Project Reference

- **Project:** PassReset (self-service AD password change portal)
- **Core value:** Reliable, secure, self-service password change that fits corporate AD environments without bespoke deployment engineering
- **Baseline version:** v1.4.0
- **Current milestone:** v2.0.0 (Platform evolution) — ✅ SHIPPED
- **Milestone chain:** v1.2.3 ✅ → v1.3.0 ✅ → v1.3.1 ✅ → v1.3.2 ✅ → v1.4.0 ✅ → v2.0.0 ✅ → v2.0.1 ✅
- **Current focus:** v2.0 complete. All four phases (11–14) shipped; production release **v2.0.1** published 2026-06-01.

## Current Position

Phase: 14 (Pluggable Windows hosting modes) — ✅ SHIPPED in v2.0.0
Milestone: v2.0.0 — 4/4 phases complete (11 ✓, 12 ✓, 13 ✓, 14 ✓)
Release: **v2.0.1** in production (GitHub Release published with `PassReset-v2.0.1.zip`)
Next: No active milestone. Next work starts a new milestone from the backlog.

- **Status:** v2.0 GA shipped. The alpha chain (2.0.0-alpha.1 … alpha.8) consolidated into a single `[2.0.0]` CHANGELOG entry; production cut as v2.0.1 after two CI-only defects (flaky frontend test + npm-audit gate exit code) were fixed.
- **Progress:** v2.0 [██████████] 100% (4/4 phases)

## Milestone Map

| Milestone | Phases | Status |
|---|---|---|
| v1.2.3 | 01 | ✅ Shipped 2026-04-14 (archived) |
| v1.3.0 | 02, 03 | ✅ Shipped 2026-04-15 (archived) |
| v1.3.1 | 07 (legacy) | ✅ Shipped 2026-04-15 (archived) |
| v1.3.2 | 07 (code review fix rollup) | ✅ Shipped 2026-04-16 (archived) |
| v1.4.0 | 7 ✓, 8 ✓, 9 ✓, 10 ✓ | ✅ Code-complete |
| v2.0.0 | 11 ✓, 12 ✓, 13 ✓, 14 ✓ | ✅ Shipped — alpha chain consolidated, GA cut as v2.0.1 (2026-06-01) |

> Note: legacy phase 07 numbering belongs to the archived v1.3.1/v1.3.2 milestones. The v1.4.0 chain restarts active phase numbering at 7; archived directories are not affected.

## Performance Metrics

- Phases complete: 8/8 active (01, 02, 03, legacy 07, 11, 12, 13, 14) + v1.4.0 phases 7–10
- Requirements delivered: BUG-001..004, QA-001, FEAT-001..004, STAB-001..021, V2-001..003 — all mapped requirements delivered
- Releases shipped: v1.2.3, v1.3.0, v1.3.1, v1.3.2, v1.4.x, v2.0.0-alpha.1…alpha.8, **v2.0.1 (GA)**

## Accumulated Context

### Key Decisions

- **2026-04-13:** MSI packaging rolled back; PowerShell installer is the supported deployment path
- **2026-04-16:** Inserted v1.4.0 stabilization milestone before v2.0 — 21 GitHub issues represented install/security regressions
- **2026-04-21 (Phase 11 ship):** `PasswordChangeOptions` relocated to `PassReset.Common` (platform-neutral); `ProviderMode` enum (Auto/Windows/Ldap, default Auto) added; Windows provider preserved byte-for-byte; conditional TFM on `PassReset.Web` + `PassReset.Tests` because NU1201 blocks pure net10.0 referencing net10.0-windows projects
- **Phase 12:** Local offline password policy delivered as `LocalPolicyPasswordChangeProvider` (outermost decorator) — banned-words list + local HIBP SHA-1 corpus; `ApiErrorCode.BannedWord` (20) / `LocallyKnownPwned` (21)
- **Phase 13:** Loopback admin UI at `127.0.0.1:<LoopbackPort>` on a dedicated Kestrel listener; encrypted secret storage (`secrets.dat`) via ASP.NET Core Data Protection; opt-in (`AdminSettings.Enabled` default false)
- **Phase 14:** Pluggable Windows hosting modes (`-HostingMode IIS|Service|Console`, IIS default); installer migrated `WebAdministration` → `IISAdministration` for PowerShell 7 compatibility; self-signed cert auto-generation fallback
- **2026-06-01 (GA):** v2.0.0 tag's release run failed on two pre-existing CI defects and the tag is immutable (repo ruleset), so production was cut as **v2.0.1**. The two defects: a flaky `PasswordForm` test (HIBP debounce stealing a single-shot fetch mock — fixed via `mockFetchByUrl`) and the npm-audit gate leaking `npm audit`'s non-zero exit code (fixed via `$LASTEXITCODE` reset + explicit `exit 0`)

### Known Limitations (carried into v2.0 GA)

- **Linux web-host deployment still blocked** by the Phase-11 conditional TFM (NuGet refuses to restore a net10.0 project with a ProjectReference to a net10.0-windows one even behind a `<Condition>`). LDAP provider, local policy, admin UI, and Service hosting are Windows-ready; full Linux hosting needs `PassReset.PasswordProvider` multi-targeting (follow-up).
- **`UserCannotChangePassword` ACE check deferred on the LDAP provider** — AD's server-side modify rejection still enforces it; the error message is less specific on Linux.

### Blockers

- None

### Notes

- v2.0 phase numbering: 11 (Multi-OS LDAP), 12 (Local Password DB), 13 (Secure Config / Admin UI), 14 (Hosting Modes)
- Reusable workflows (`uses: ./.github/workflows/tests.yml`) require explicit `secrets: inherit` from the caller; landed in Phase 11
- The `v2.0.0` git tag is a dangling tag (no GitHub Release was published from it); `v2.0.1` is the canonical production release

## Session Continuity

- **2026-04-21:** Shipped Phase 11 as 2.0.0-alpha.1.
- **2026-04-22 → 04-24:** Phases 12–14 plus eight installer-focused alphas (alpha.1 … alpha.8) hardening the PowerShell 7 / IISAdministration migration and adding the self-signed cert fallback.
- **2026-06-01:** Promoted the alpha chain to production. Consolidated the eight alpha CHANGELOG entries into a single `[2.0.0]` section, synced SECURITY.md / UPGRADING.md, cut the tag, root-caused two CI-only failures, and shipped **v2.0.1** GA. See `tasks/lessons.md` 2026-06-01 entry (local "green" ≠ CI "green").
- **Next session:** No active milestone — start a new one from the backlog when ready.
