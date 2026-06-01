# GitHub Issues vs. Current Code — Verification Audit

**Date:** 2026-06-01
**Method:** 21 parallel verifier agents (one per open issue) read each issue's acceptance criteria and proved them against current code (`master @ d8a6f42`); FIXED verdicts then went through an adversarial refutation pass.
**Scope:** All open issues #19–#39 (= STAB-001..021).

## Headline

- **5 FIXED** (survived adversarial refutation) — safe to close.
- **16 PARTIAL** — core behavior present, but ≥1 acceptance criterion unmet.
- **0 OPEN / 0 CANNOT_VERIFY.**

The adversarial pass **refuted 7 initial FIXED verdicts** (#21, #28, #31, #32, #33, #36, #38), downgrading them to PARTIAL. This contradicts the `.planning` docs (updated earlier today) that marked all STAB requirements "delivered" — the core of each shipped, but acceptance criteria were not fully met.

## Verdicts

| Issue | STAB | Verdict | Gap class | One-line gap |
|---|---|---|---|---|
| #22 | STAB-007 | ✅ FIXED | — | Template is pure JSON; Test-Json gates publish+upgrade |
| #23 | STAB-003 | ✅ FIXED | — | AppPool identity read via IISAdministration config API; no `.Value` warning |
| #25 | STAB-009 | ✅ FIXED | — | Installer Test-Json + 9 `IValidateOptions` w/ `ValidateOnStart` |
| #35 | STAB-020 | ✅ FIXED | — | CI runs gated `npm audit` + `dotnet --vulnerable` |
| #37 | STAB-012 | ✅ FIXED | — | Drift check runs unconditionally; no silent-skip |
| #20 | STAB-002 | ⚠️ PARTIAL | minor | Reconfigure detected, but success banner still says "upgraded successfully"; stale `-Reconfigure` doc |
| #33 | STAB-017 | ⚠️ PARTIAL | minor/doc | Env-var sourcing works + tested; `docs/Secret-Management.md:65` is stale/contradictory |
| #34 | STAB-019 | ⚠️ PARTIAL | minor | Post-deploy /health check works for IIS; not wired for Service/Console mode; no log-location hints |
| #39 | STAB-005 | ⚠️ PARTIAL | test/doc | Uninstall parses + works; no CI parse-check gate to prevent shipping broken scripts |
| #19 | STAB-001 | ⚠️ PARTIAL | **behavioral** | Alt-port path bug: `$HttpPort` not reassigned to `$selectedHttpPort` → re-binds `*:80:` when a cert is supplied |
| #21 | STAB-006 | ⚠️ PARTIAL | **behavioral** | No post-DISM re-check; .NET Hosting Bundle never auto-installed (prints URL); no-IIS abort precedes auto-install |
| #24 | STAB-010 | ⚠️ PARTIAL | **behavioral** | Config sync gated to IIS mode only (Service/Console get none); no true dry-run; no per-file backup |
| #26 | STAB-011 | ⚠️ PARTIAL | **behavioral** | No dry-run/diff mode (`-WhatIf` not honored by sync); `-ConfigSync` undocumented |
| #27 | STAB-008 | ⚠️ PARTIAL | **behavioral** | Schema omits `LocalPolicy`, `AdminSettings`, `Kestrel` sections (shipped in template) → sync can't manage them |
| #28 | STAB-013 | ⚠️ PARTIAL | **behavioral** | Enumeration oracle remains: `ApproachingLockout`/`PortalLockout` returned only for existing users (default-on) |
| #29 | STAB-014 | ⚠️ PARTIAL | **behavioral** | reCAPTCHA missing/low-score/unreachable paths untestable (hard-wired HttpClient, no seam) → 3 of 5 scenarios uncovered |
| #30 | STAB-015 | ⚠️ PARTIAL | **behavioral** | `AuditEvent` DTO/formatter exist but are **never wired** into the controller — dead code at runtime |
| #31 | STAB-018 | ⚠️ PARTIAL | **behavioral** | When expiry service enabled, fresh deploy → `/health` 503, breaking the STAB-019 post-deploy check |
| #32 | STAB-016 | ⚠️ PARTIAL | **behavioral** | Self-signed default + HTTPS-first → STAB-019 TLS validation fails; no redirect for non-443 HTTPS port |
| #36 | STAB-004 | ⚠️ PARTIAL | **behavioral** | Pre-check works, but the E_ACCESSDENIED `catch` can't catch AccountManagement's `UnauthorizedAccessException` |
| #38 | STAB-021 | ⚠️ PARTIAL | **behavioral** | LDAP provider hard-codes complexity=false/history=0 → wrong policy shown on `ProviderMode:Ldap`; no FGPP; min-age never rendered |

## Notable cross-issue couplings

- **#31 ↔ #34 ↔ #32:** the post-deploy health check (#34) can be broken by *both* the expiry-service-enabled 503 (#31) and the self-signed-cert TLS failure (#32). These interact — fixing #34 robustly requires addressing both.
- **#27 (schema gaps) ↔ #24/#26 (sync):** the schema omits the v2.0 sections (`LocalPolicy`, `AdminSettings`, `Kestrel`), so the config-sync engine literally cannot add or manage their keys — #27 is a prerequisite for #24/#26 being complete.
- **#30:** the structured-audit infrastructure is built and unit-tested but never called from production code — the highest "looks done, isn't wired" risk.

## Recommendation

- **Close now (with a comment citing the implementing code):** #22, #23, #25, #35, #37.
- **Keep open, scope a follow-up:** the 16 PARTIALs. Of these, the **behavioral** ones (#19, #21, #24, #26, #27, #28, #29, #30, #31, #32, #36, #38) have real code defects or missing wiring; the **minor/test-doc** ones (#20, #33, #34, #39) are close to done and could be finished quickly.

*Generated from workflow run `wf_764677b2-437`.*
