# PassReset — Project Context

**Initialized:** 2026-04-14 (brownfield)
**Current version:** v2.0.1 (GA, 2026-06-01)
**Status:** v2.0 shipped — no active milestone

## What This Is

PassReset is a self-service Active Directory password change portal. Users enter their current credentials and a new password; the backend authenticates against AD via LDAP(S) and performs the change.

**Stack (as of v2.0):**
- Backend: ASP.NET Core 10. Core/provider code is `net10.0` (platform-neutral); the Windows provider and web host carry a `net10.0-windows` target. Cross-platform AD via `System.DirectoryServices.Protocols` (LDAP provider); Windows via `System.DirectoryServices.AccountManagement`. MailKit for email.
- Frontend: React 19 + MUI 6 + Vite + TypeScript 5.8
- Hosting: IIS (default), Windows Service, or Console — selectable at install time. Linux/Docker web hosting is designed-for but not yet unblocked (see Context).
- CI: GitHub Actions on `windows-latest`

Full architecture, conventions, and build commands are captured in `./CLAUDE.md` (local-only, gitignored). The public face lives in `README.md` and `docs/`.

## Core Value

A reliable, secure, self-service password reset portal that fits into corporate AD environments without bespoke deployment engineering — usable in air-gapped/internal-CA environments, observable via SIEM, and safe against credential brute-force.

## Context

- **Prior rollback (2026-04-13):** An earlier v2.0 effort focused on MSI packaging was rolled back; the hardened PowerShell installer remains the supported deployment path. (The v2.0 that actually shipped is the Platform-Evolution milestone below, not the MSI one.)
- **Test foundation in place:** xUnit (backend) + Vitest/RTL (frontend), gating release via the reusable `tests.yml` workflow. Delivered in v1.3 (QA-001).
- **Deployment:** PowerShell installer (`deploy/Install-PassReset.ps1`) + zip artifact. As of v2.0, `-HostingMode IIS|Service|Console` and a self-signed-cert fallback are supported.
- **Linux web hosting:** Designed-for (LDAP provider, local policy, admin UI, Service hosting are platform-neutral) but the web host can't yet restore on Linux — a `net10.0` → `net10.0-windows` ProjectReference (NU1201) blocks it until `PassReset.PasswordProvider` is multi-targeted. Tracked as a v2.x follow-up.

## Requirements

### Validated (existing capabilities — brownfield)

- ✓ Self-service password change against AD (provider pattern: real AD + debug + lockout decorator) — existing
- ✓ Per-IP rate limiting (5 req / 5 min on POST `/api/password`) — existing
- ✓ Per-user portal lockout tracked in-memory (threshold + window) — existing
- ✓ HaveIBeenPwned breach check via `PwnedPasswordChecker` (k-anonymity API) — existing
- ✓ reCAPTCHA v3 bot prevention (optional) — existing
- ✓ Password strength meter (zxcvbn) + Levenshtein distance validation — existing
- ✓ SIEM integration: 10 event types via RFC 5424 syslog + optional email alerts — existing
- ✓ Password expiry notification background service (daily, group-scoped) — existing
- ✓ Fire-and-forget email notification on successful change (MailKit/SMTP) — existing
- ✓ Security headers (CSP, HSTS, X-Frame-Options, nosniff, Referrer-Policy, Permissions-Policy) — existing
- ✓ `/api/health` endpoint with AD connectivity check — existing
- ✓ Client settings flow: server → `GET /api/password` → `useSettings()` hook (single source) — existing
- ✓ Dark mode via `prefers-color-scheme`; MUI theme with teal primary (`#0b6366`) — existing
- ✓ PowerShell installer with config preservation + rollback (hardened in v1.2.2) — existing
- ✓ Uninstall-PassReset.ps1 parses on PS 5.1 + 7.x (UTF-8 BOM + ASCII dividers) — Phase 7 (STAB-005, gh#39) 2026-04-16
- ✓ Install-PassReset.ps1 port-80 conflict detection + reachable URL announce — Phase 7 (STAB-001, gh#19) 2026-04-16
- ✓ Install-PassReset.ps1 same-version reconfigure branch (no "upgrade" wording, file mirror skipped) — Phase 7 (STAB-002, gh#20) 2026-04-16
- ✓ Install-PassReset.ps1 AppPool identity read via Get-WebConfigurationProperty (fixes PS 7.x regression) — Phase 7 (STAB-003, gh#23) 2026-04-16
- ✓ Install-PassReset.ps1 single-DISM-prompt IIS features + clean exit on missing Hosting Bundle — Phase 7 (STAB-006, gh#21) 2026-04-16
- ✓ PasswordChangeProvider.PreCheckMinPwdAge: consecutive-change pre-check → PasswordTooRecentlyChanged (19) — Phase 7 (STAB-004, gh#36) 2026-04-16

### Delivered (v1.2.3 — Hotfix milestone, P1)

- [x] **BUG-001** SMTP SSL handshake succeeds when relay presents internal-CA cert, without silent bypass
- [x] **BUG-002** `E_ACCESSDENIED (0x80070005)` from AD min-pwd-age maps to `ApiErrorCode.PasswordTooRecentlyChanged` with user-friendly message
- [x] **BUG-003** `Install-PassReset.ps1` preserves existing IIS AppPool identity on upgrade

### Delivered (v1.3.0 — UX + Quality milestone)

- [x] **FEAT-001** Branding surfaces (company/portal name, helpdesk URL/email, usage text, logo + favicon) via `ClientSettings`
- [x] **FEAT-002** Display effective AD password policy (min requirements), toggleable, default off, fails closed
- [x] **FEAT-003** Clipboard protection — clear clipboard N seconds after password generator use (if still matches)
- [x] **FEAT-004** HIBP breach status indicator on new-password blur, respecting `FailOpenOnPwnedCheckUnavailable`
- [x] **QA-001** Test foundation — xUnit (backend) + Vitest/RTL (frontend), CI gates block on test failures

### v1.4.0 — Stabilization (pre-v2.0 hardening) — 🟡 Mostly delivered

Triage of 21 GitHub issues opened 2026-04-16 against v1.3.2. See REQUIREMENTS.md STAB-001..021. A 2026-06-01 issue-vs-code audit (`docs/superpowers/issue-vs-code-audit-2026-06-01.md`) found **5 verified complete** (gh#22/#23/#25/#35/#37 closed) and **16 partial** (issues kept open). Phase-level summary below; per-requirement status in REQUIREMENTS.md.

- [~] **Phase 7** — Installer & Deployment Fixes (STAB-001..006): STAB-003 verified (AppPool identity); STAB-001/002/004/005/006 partial (alt-port re-bind bug, reconfigure banner, E_ACCESSDENIED catch, no CI parse-gate, .NET bundle not auto-installed).
- [~] **Phase 8** — Configuration Schema & Sync (STAB-007..012): STAB-007/009/012 verified (pure-JSON template, validators, drift check); STAB-008/010/011 partial (schema omits v2.0 sections; sync IIS-only; no dry-run).
- [~] **Phase 9** — Security Hardening (STAB-013..017): all 5 partial — notably structured `AuditEvent` is built but never wired into production (STAB-015), and a residual lockout-based enumeration oracle remains (STAB-013).
- [~] **Phase 10** — Operational Readiness (STAB-018..021): STAB-020 verified (CI security gate); STAB-018/019/021 partial (/health 503 when expiry enabled, post-deploy IIS-only, LDAP shows wrong policy).

### Delivered (v2.0.0 — Platform evolution, GA = v2.0.1, 2026-06-01)

- [x] **V2-001** Multi-OS support — `PassReset.PasswordProvider.Ldap` (`net10.0`) implementing `IPasswordChangeProvider` over `System.DirectoryServices.Protocols`; `ProviderMode` (Auto/Windows/Ldap); Samba AD DC CI integration test. *(Phase 11)* — Linux web-host restore still blocked by NU1201 (follow-up).
- [x] **V2-002** Local password-protection DB — `LocalPolicyPasswordChangeProvider` with operator-managed banned-words list + local HIBP SHA-1 corpus; `ApiErrorCode.BannedWord` (20) / `LocallyKnownPwned` (21). *(Phase 12)*
- [x] **V2-003** Secure config storage — loopback admin UI + encrypted `secrets.dat` via ASP.NET Core Data Protection; opt-in `AdminSettings`. *(Phase 13)*
- [x] **V2-004 (added)** Pluggable Windows hosting modes — `-HostingMode IIS|Service|Console`; IISAdministration/PowerShell-7 installer migration; self-signed cert auto-generation. *(Phase 14)*

### Out of Scope

- **MSI packaging** — previously explored under a v2.0 MSI milestone (rolled back 2026-04-13); superseded by the hardened PowerShell installer.
- **Changing the tech stack** — React 19 / MUI 6 / ASP.NET Core 10 are locked for this milestone chain.
- **Password reset via email/SMS flow** — this portal is *change* only (user knows current password).
- **Identity federation / SSO adapters** — explicitly a direct-AD portal.

## Key Decisions

| Decision | Rationale | Outcome |
|---|---|---|
| Keep PowerShell installer; drop MSI | MSI path was rolled back; PS installer hardened in v1.2.2 | Accepted 2026-04-13 |
| v1.2.3 = bugs-only hotfix; v1.3 = features + tests in parallel | Ship P1 fixes fast; let QA-001 land alongside UX without blocking each other | Chosen 2026-04-14 |
| Coarse phase granularity + parallel plan execution | Fewer, broader phases fit the three-milestone structure; QA-001 runs parallel to FEAT work | Chosen 2026-04-14 |
| No tech stack changes this milestone chain | Reduce risk; features must fit current React 19 / MUI 6 / ASP.NET Core 10 | Locked |
| Balanced model profile (Sonnet) for agents | Good quality/cost for a mature brownfield project | Chosen 2026-04-14 |
| Insert v1.4.0 stabilization milestone before v2.0 | 21 GitHub issues opened against v1.3.2 represent install/security regressions blocking confident v2.0 work | Chosen 2026-04-16 |
| STAB-017 (env-var secrets) is a stepping stone, not the full V2-003 | Env vars unblock production deployments now without committing to a DPAPI/Key Vault mechanism v2.0 may revisit | Chosen 2026-04-16 |
| `ProviderMode` default `Auto` (Windows provider on Windows) | v2.0 must be a non-breaking upgrade for existing IIS/Windows installs; the Windows provider is preserved byte-for-byte | Phase 11 |
| V2-003 implemented as Data Protection (`secrets.dat`), not DPAPI/Key Vault | Cross-platform-capable, no external dependency, integrates with the loopback admin UI | Phase 13 |
| v2.0 GA cut as **v2.0.1**, not v2.0.0 | The v2.0.0 tag's release run failed on two pre-existing CI defects, and the tag is immutable (repo ruleset); v2.0.1 carries identical app code plus the green pipeline | 2026-06-01 |

## Evolution

This document is maintained at milestone boundaries. When a milestone ships: move delivered requirements to Delivered (with phase reference), retire stale Out-of-Scope reasons, log new Key Decisions, and update Context to current state.

---
*Last updated: 2026-06-01 — v2.0.0 (Platform Evolution) shipped; Phases 11–14 delivered; production GA published as v2.0.1.*
