# PassReset — Requirements

**Active milestone:** none — v2.0.0 shipped (GA = v2.0.1, 2026-06-01)
**Prior milestones:** v1.2.3 ✅ · v1.3.0 ✅ · v1.3.1 ✅ · v1.3.2 ✅ · v1.4.0 🟡 (5/21 STAB verified complete, 16 partial — open) · v2.0.0 ✅ (see `milestones/`)
**Last updated:** 2026-06-01

> REQ IDs are stable references used by `ROADMAP.md` and phase plans.
> Delivered requirements live in `milestones/v{version}-REQUIREMENTS.md`.

---

## v1.4.0 — Stabilization (pre-v2.0 hardening) — 🟡 Mostly delivered

Source: 21 GitHub issues (#19–#39) opened 2026-04-16. A 2026-06-01 issue-vs-code audit (see `docs/superpowers/issue-vs-code-audit-2026-06-01.md`) found **5 fully delivered** (gh#22/#23/#25/#35/#37 closed) and **16 partial** — core behavior present, ≥1 acceptance criterion unmet, issues kept open.
Legend: `[x]` verified complete · `[~]` partial (gap noted, GitHub issue still open).

### Installer & Deployment Fixes (Phase 7)

- [~] **STAB-001** (gh#19): Port-80 conflict detection — *partial:* alt-port path bug — `$HttpPort` not reassigned to `$selectedHttpPort`, so it re-binds `*:80:` when a cert is supplied; no test coverage of the IIS conflict block.
- [~] **STAB-002** (gh#20): Same-version "re-configure" prompt — *partial:* detection + prompt + file-mirror-skip done, but success banner still prints "upgraded successfully" on reconfigure; stale `-Reconfigure` doc references.
- [x] **STAB-003** (gh#23): Existing AppPool identity read via IISAdministration config API; no `.Value` warning. ✅ verified, closed.
- [~] **STAB-004** (gh#36): Consecutive-change min-pwd-age — *partial:* `PreCheckMinPwdAge` handles the common case, but the E_ACCESSDENIED `catch` cannot catch AccountManagement's `UnauthorizedAccessException`; SIEM event type is `Generic`.
- [~] **STAB-005** (gh#39): `Uninstall-PassReset.ps1` parses + works with `-KeepFiles` — *partial:* no CI parse-check / PSScriptAnalyzer gate to prevent shipping a broken script (the issue's "mandatory release quality gate").
- [~] **STAB-006** (gh#21): Prerequisite detection — *partial:* IIS features auto-installable, but .NET Hosting Bundle is detect-only (prints URL); no post-DISM re-check; no-IIS abort precedes the auto-install path.

### Configuration Schema & Sync (Phase 8)

- [x] **STAB-007** (gh#22): `appsettings.Production.template.json` is pure JSON; `Test-Json` gates publish + upgrade. ✅ verified, closed.
- [~] **STAB-008** (gh#27): Authoritative JSON Schema exists — *partial:* schema omits the v2.0 `LocalPolicy`, `AdminSettings`, and `Kestrel` sections (shipped in the template), so config-sync can't manage them.
- [x] **STAB-009** (gh#25): Installer `Test-Json` pre-flight + 9 `IValidateOptions<T>` with `ValidateOnStart`. ✅ verified, closed.
- [~] **STAB-010** (gh#24): Additive config sync — *partial:* gated to IIS mode only (Service/Console get no sync); no true dry-run; no per-file backup of `appsettings.Production.json`.
- [~] **STAB-011** (gh#26): `-ConfigSync` controls — *partial:* no dry-run/diff mode (`-WhatIf` not honored by the sync); `-ConfigSync` undocumented in operator help.
- [x] **STAB-012** (gh#37): Schema-drift check runs unconditionally, no silent-skip. ✅ verified, closed.

### Security Hardening (Phase 9)

- [~] **STAB-013** (gh#28): Production error collapse — *partial:* `InvalidCredentials`/`UserNotFound` collapse to `Generic`, but `ApproachingLockout`/`PortalLockout` are returned only for *existing* users (default-on) — a residual enumeration oracle.
- [~] **STAB-014** (gh#29): Rate-limit + reCAPTCHA enforced — *partial:* 3 of 5 required test scenarios (captcha-missing, low-score, provider-unreachable) are uncovered; HttpClient is hard-wired with no injectable seam.
- [~] **STAB-015** (gh#30): Structured `AuditEvent` DTO + formatter + redaction tests exist — *partial:* **never wired into production** (no controller/middleware calls `LogEvent(AuditEvent)`); traceId never reaches SIEM; no "attempt started" event.
- [~] **STAB-016** (gh#32): HTTPS-only + HSTS implemented — *partial:* self-signed default + HTTPS-first breaks the STAB-019 post-deploy TLS check; no HTTP→HTTPS redirect for non-443 HTTPS ports; no binding host/port/cert match validation.
- [~] **STAB-017** (gh#33): Env-var secret sourcing works end-to-end + tested — *partial:* `docs/Secret-Management.md:65` is stale/contradictory (claims installer does NOT set the env vars, but it does).

### Operational Readiness (Phase 10)

- [~] **STAB-018** (gh#31): `/api/health` per-dependency checks (AD/SMTP/expiry) — *partial:* with the expiry service enabled, a fresh deploy reports `expiryService=degraded` → `/api/health` 503, breaking the STAB-019 check; no toggle to disable an active connectivity probe independently.
- [~] **STAB-019** (gh#34): Post-deploy `/api/health` + `GET /api/password` verification — *partial:* IIS-mode only (Service/Console get none); no log-location hints in failure output; no custom Host-header support.
- [x] **STAB-020** (gh#35): CI runs gated `npm audit` + `dotnet --vulnerable`. ✅ verified, closed.
- [~] **STAB-021** (gh#38): Effective AD policy displayed — *partial:* the LDAP provider hard-codes `complexity=false`/`history=0`, so `ProviderMode:Ldap` shows wrong values; no FGPP/PSO support; min-age never rendered client-side.

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
| STAB-003 | Phase 7 (Installer & Deployment Fixes) | ✅ Delivered (gh#23 closed) |
| STAB-001/002/004/005/006 | Phase 7 | 🟡 Partial (gh#19/#20/#36/#39/#21 open) |
| STAB-007, STAB-009, STAB-012 | Phase 8 (Configuration Schema & Sync) | ✅ Delivered (gh#22/#25/#37 closed) |
| STAB-008/010/011 | Phase 8 | 🟡 Partial (gh#27/#24/#26 open) |
| STAB-013..017 | Phase 9 (Security Hardening) | 🟡 Partial (gh#28/#29/#30/#32/#33 open) |
| STAB-020 | Phase 10 (Operational Readiness) | ✅ Delivered (gh#35 closed) |
| STAB-018/019/021 | Phase 10 | 🟡 Partial (gh#31/#34/#38 open) |
| V2-001 | Phase 11 (Cross-platform LDAP provider) | ✅ Delivered (v2.0.1) |
| V2-002 | Phase 12 (Local Password DB) | ✅ Delivered (v2.0.1) |
| V2-003 | Phase 13 (Secure Config Storage + Admin UI) | ✅ Delivered (v2.0.1) |
| V2-004 | Phase 14 (Pluggable Windows hosting modes) | ✅ Delivered (v2.0.1) |

**Coverage:** 25/25 requirements mapped ✓ · 0 orphans
**Delivery (per 2026-06-01 issue-vs-code audit):** v2.0 (V2-001..004) delivered & shipped; v1.4.0 STAB = 5 verified complete, 16 partial (GitHub issues kept open). See `docs/superpowers/issue-vs-code-audit-2026-06-01.md`.
