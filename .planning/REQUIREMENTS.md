# PassReset — Requirements

**Active milestone:** none — v2.0.0 shipped (GA = v2.0.1, 2026-06-01)
**Prior milestones:** v1.2.3 ✅ · v1.3.0 ✅ · v1.3.1 ✅ · v1.3.2 ✅ · v1.4.0 ✅ · v2.0.0 ✅ (see `milestones/`)
**Last updated:** 2026-06-01

> REQ IDs are stable references used by `ROADMAP.md` and phase plans.
> Delivered requirements live in `milestones/v{version}-REQUIREMENTS.md`.

---

## v1.4.0 — Stabilization (pre-v2.0 hardening) — ✅ Shipped

Source: 21 GitHub issues (#19–#39) opened 2026-04-16. All STAB requirements delivered in the v1.4.x line.

### Installer & Deployment Fixes (Phase 7) ✅

- [x] **STAB-001** (gh#19): Fresh install must not fail when port 80 is already bound by IIS Default Web Site — installer detects conflict and either reuses bindings or prompts for an alternate port.
- [x] **STAB-002** (gh#20): Re-running installer with the same version must prompt "re-configure" (not "upgrade") since no upgrade is occurring.
- [x] **STAB-003** (gh#23): Upgrade to 1.3.2+ must read the existing AppPool identity correctly without warning + fallback (BUG-003 hardening regression).
- [x] **STAB-004** (gh#36): Two consecutive password changes for the same user must not raise `UnauthorizedAccessException (E_ACCESSDENIED)`; surface a clear UI error mapped to `ApiErrorCode.PasswordTooRecentlyChanged` if min-pwd-age trips, otherwise no generic crash.
- [x] **STAB-005** (gh#39): `Uninstall-PassReset.ps1` must parse and execute cleanly (currently fails with `MissingEndCurlyBrace` ParserError); supports `-KeepFiles` and removes IIS site + AppPool.
- [x] **STAB-006** (gh#21): `Install-PassReset.ps1` must detect missing IIS roles/features and .NET 10 hosting bundle, and offer interactive install per `docs/IIS-Setup.md`.

### Configuration Schema & Sync (Phase 8)

- [x] **STAB-007** (gh#22): Generated `appsettings.Production.json` must be valid JSON — strip comments from Serilog and Branding sections (or move them into a sibling `.template.json`).
- [x] **STAB-008** (gh#27): Provide an authoritative configuration schema/manifest defining all valid keys, types, and defaults; enables validation and safe removal of obsolete keys.
- [x] **STAB-009** (gh#25): Pre-flight configuration validation runs at install/startup — fails fast with actionable errors when `appsettings.Production.json` is structurally invalid or internally inconsistent.
- [x] **STAB-010** (gh#24): Upgrade syncs `appsettings.Production.json` against current schema — adds missing keys with documented defaults, flags obsolete keys, never silently destroys operator overrides.
- [x] **STAB-011** (gh#26): Upgrade exposes explicit controls (flag and/or interactive prompt) governing config-sync behavior — operators choose between manual review, auto-merge-additions, or full sync.
- [x] **STAB-012** (gh#37): Upgrade schema-drift check must succeed even when `appsettings.Production.json` contains comment blocks (depends on STAB-007 fix or comment-tolerant parser).

### Security Hardening (Phase 9)

- [x] **STAB-013** (gh#28): `POST /api/password` in production never reveals account existence or exact failure reason — `InvalidCredentials` and `UserNotFound` map to a single generic error code in production responses (server-side SIEM still distinguishes).
- [x] **STAB-014** (gh#29): Rate limiting and reCAPTCHA v3 enforcement on `POST /api/password` is explicit, consistent, and covered by integration tests; behavior with reCAPTCHA disabled is also tested.
- [x] **STAB-015** (gh#30): Structured audit/security event trail covers attempts, failures, rate-limit blocks, and successes with strict secret redaction (passwords/tokens never appear in logs).
- [x] **STAB-016** (gh#32): HTTPS-first behavior is enforced — automatic HTTP→HTTPS redirect, correct HSTS header, and IIS bindings cannot accidentally expose the app on plain HTTP.
- [x] **STAB-017** (gh#33): SMTP, LDAP, and reCAPTCHA secrets can be sourced from environment variables (or .NET user-secrets in dev) instead of plaintext `appsettings.Production.json`. Stepping stone toward V2-003; full encrypted secret storage delivered in v2.0 Phase 13 (Data Protection `secrets.dat`).

### Operational Readiness (Phase 10)

- [x] **STAB-018** (gh#31): `/api/health` reports readiness of AD, SMTP, and the password-expiry background service without leaking secrets — distinct healthy/degraded/unhealthy states per dependency.
- [x] **STAB-019** (gh#34): `Install-PassReset.ps1` post-deploy verification calls `/api/health` and `GET /api/password`; fails the install with a clear message when either endpoint does not respond as expected.
- [x] **STAB-020** (gh#35): CI runs build + minimal security checks (`npm audit`, `dotnet list package --vulnerable`) on every push and PR; fails on high-severity vulnerabilities with a documented exception process.
- [x] **STAB-021** (gh#38): Display the effective AD password policy (or a clear summary) to the user before they attempt a change — reduces failed attempts and confusion. *(UX continuation of FEAT-002.)*

---

## v2.0.0 — Platform evolution — ✅ Shipped (GA = v2.0.1, 2026-06-01)

- [x] **V2-001** *(Phase 11)*: Multi-OS support — `PassReset.PasswordProvider.Ldap` (`net10.0`) implements `IPasswordChangeProvider` over `System.DirectoryServices.Protocols`; `ProviderMode` (Auto/Windows/Ldap) selects at runtime; Samba AD DC CI integration test passes the shared behavioral contract. Linux web-host *restore* remains blocked by NU1201 (conditional TFM) — tracked as a v2.x follow-up; the provider layer itself is platform-neutral.
- [x] **V2-002** *(Phase 12)*: Local password-protection database — `LocalPolicyPasswordChangeProvider` (outermost decorator) consults an operator-managed banned-words list + a local HIBP SHA-1 corpus before any AD round-trip; enforces bans even when stricter than AD policy. `ApiErrorCode.BannedWord` (20) / `LocallyKnownPwned` (21).
- [x] **V2-003** *(Phase 13)*: Secure config storage — secrets moved out of cleartext `appsettings.Production.json` into an encrypted `secrets.dat` via ASP.NET Core Data Protection, managed through an opt-in loopback admin UI (`AdminSettings.Enabled`, default false). Env-var overrides still take precedence; documented upgrade path.
- [x] **V2-004** *(Phase 14, added during v2.0)*: Pluggable Windows hosting modes — `-HostingMode IIS|Service|Console` (IIS default); installer migrated `WebAdministration` → `IISAdministration` for PowerShell 7; self-signed certificate auto-generation fallback (`-AllowSelfSignedCertificate`).

---

## Cross-cutting constraints (apply to every requirement)

- **No breaking config changes** for operators upgrading from v1.3.x unless explicitly documented in `UPGRADING.md`.
- **Commit convention** enforced by `.githooks/commit-msg` — types: `feat fix refactor docs chore test ci perf style`; scopes: `web provider common deploy docs ci deps security installer`.
- **CI**: GitHub Actions on `windows-latest`; release triggered by `git tag vX.Y.Z` → `release.yml`. Tests gate release via reusable `tests.yml`.
- **Tech stack**: ASP.NET Core 10 / React 19 / MUI 6 / Vite. v2.0 may introduce cross-platform infrastructure (Novell LDAP, Docker) but must not break the existing Windows/IIS deployment path.
- **Documentation**: `README.md`, `CHANGELOG.md`, and affected `docs/*.md` updated as part of each release.

---

## Out of Scope

- **MSI packaging** — deferred after the 2026-04-13 rollback. PowerShell installer remains the supported deployment path.
- **Password reset via email/SMS** — portal is *change* only.
- **SSO / federation adapters** — direct-AD portal.
- **Stack modernization** (e.g., migrating to .NET 11, React 20) — not part of this milestone.

---

## Traceability

| REQ-ID | Phase | Status |
|---|---|---|
| STAB-001..006 | Phase 7 (Installer & Deployment Fixes) | ✅ Delivered (v1.4.x) |
| STAB-007..012 | Phase 8 (Configuration Schema & Sync) | ✅ Delivered (v1.4.x) |
| STAB-013..017 | Phase 9 (Security Hardening) | ✅ Delivered (v1.4.x) |
| STAB-018..021 | Phase 10 (Operational Readiness) | ✅ Delivered (v1.4.x) |
| V2-001 | Phase 11 (Cross-platform LDAP provider) | ✅ Delivered (v2.0.1) |
| V2-002 | Phase 12 (Local Password DB) | ✅ Delivered (v2.0.1) |
| V2-003 | Phase 13 (Secure Config Storage + Admin UI) | ✅ Delivered (v2.0.1) |
| V2-004 | Phase 14 (Pluggable Windows hosting modes) | ✅ Delivered (v2.0.1) |

**Coverage:** 25/25 requirements mapped ✓ · 0 orphans · all delivered
