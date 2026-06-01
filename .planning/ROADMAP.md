# PassReset — Roadmap

**Milestone chain:** v1.2.3 ✅ → v1.3.0 ✅ → v1.3.1 ✅ → v1.3.2 ✅ → v1.4.0 ✅ → v2.0.0 ✅ (GA cut as v2.0.1)
**Granularity:** coarse
**Parallelization:** enabled
**Created:** 2026-04-14
**Last updated:** 2026-06-01

## Shipped Milestones

- ✅ **v1.2.3 Hotfix** (2026-04-14) — 3 P1 bugs fixed. See [`milestones/v1.2.3-ROADMAP.md`](milestones/v1.2.3-ROADMAP.md).
- ✅ **v1.3.0 Test Foundation + UX Features** (2026-04-15) — QA-001 + FEAT-001..004. See [`milestones/v1.3.0-ROADMAP.md`](milestones/v1.3.0-ROADMAP.md).
- ✅ **v1.3.1 AD Diagnostics** (2026-04-15) — BUG-004. See [`milestones/v1.3.1-ROADMAP.md`](milestones/v1.3.1-ROADMAP.md).
- ✅ **v1.3.2 Diagnostics Code Review Fixes** (2026-04-16) — WR-01..03 rollup on top of v1.3.1. See [`milestones/v1.3.2-ROADMAP.md`](milestones/v1.3.2-ROADMAP.md).
- ✅ **v1.4.0 Stabilization** (2026-04) — STAB-001..021 across Phases 7–10 (installer, config schema/sync, security hardening, operational readiness).
- ✅ **v2.0.0 Platform Evolution** (2026-06-01, GA cut as **v2.0.1**) — Phases 11–14: cross-platform LDAP provider, local offline password policy, loopback admin UI + encrypted secrets, pluggable Windows hosting modes.

## v1.4.0 (Stabilization) — ✅ Complete

Source: 21 GitHub issues (#19–#39) opened 2026-04-16.

- [x] **Phase 7: Installer & Deployment Fixes** — STAB-001..006 (gh#19, #20, #21, #23, #36, #39) ✓ 2026-04-16
- [x] **Phase 8: Configuration Schema & Sync** — STAB-007..012 (gh#22, #24, #25, #26, #27, #37) ✓
- [x] **Phase 9: Security Hardening** — STAB-013..017 (gh#28, #29, #30, #32, #33) ✓
- [x] **Phase 10: Operational Readiness** — STAB-018..021 (gh#31, #34, #35, #38) ✓ 2026-04-20

## v2.0.0 (Platform evolution) — ✅ Shipped (GA = v2.0.1)

- [x] **Phase 11: Cross-platform LDAP provider** — `PassReset.PasswordProvider.Ldap` + `ProviderMode` (Auto/Windows/Ldap) *(was Phase 4)* ✓
- [x] **Phase 12: Local Password DB** — operator-managed banned-words + local HIBP corpus via `LocalPolicyPasswordChangeProvider` *(was Phase 5)* ✓
- [x] **Phase 13: Secure Config Storage + Admin UI** — loopback admin UI + encrypted `secrets.dat` via Data Protection *(was Phase 6)* ✓
- [x] **Phase 14: Pluggable Windows hosting modes** — `-HostingMode IIS|Service|Console` + IISAdministration/PS7 migration + self-signed cert fallback ✓

## Phase Details

### Phase 7: Installer & Deployment Fixes
**Goal**: Installer and uninstaller are reliable across fresh-install, same-version re-run, and upgrade scenarios; no generic crashes during normal password-change flows
**Depends on**: v1.3.2 (shipped)
**Parallel with**: Phase 8 (config work overlaps installer work; coordinate on appsettings touch points)
**Target release**: v1.4.0
**Requirements**: STAB-001, STAB-002, STAB-003, STAB-004, STAB-005, STAB-006
**Success Criteria** (what must be TRUE):
  1. Fresh install on a box with IIS Default Web Site bound to port 80 succeeds without manual intervention (gh#19)
  2. Re-running the installer with the current installed version prompts "re-configure", not "upgrade" (gh#20)
  3. Upgrade preserves the existing AppPool identity without warning + fallback (gh#23)
  4. Two consecutive password changes for the same user produce a clear UI error (mapped error code, no `UnauthorizedAccessException`) (gh#36)
  5. `Uninstall-PassReset.ps1` parses cleanly and removes IIS site + AppPool, with `-KeepFiles` honored (gh#39)
  6. `Install-PassReset.ps1` detects missing IIS roles/.NET 10 hosting bundle and offers interactive install (gh#21)
**Plans**: 07-01 (STAB-005 uninstaller parser), 07-02 (STAB-004 consecutive-change pre-check), 07-03 (STAB-001 port-80 + STAB-006 DISM auto-install), 07-04 (STAB-002 reconfigure + STAB-003 AppPool read)
**Status**: Complete 2026-04-16 — 6/6 code-level must-haves verified; operator runtime UAT persisted to 07-01/07-03/07-04 HUMAN-UAT.md

### Phase 8: Configuration Schema & Sync
**Goal**: `appsettings.Production.json` is governed by an authoritative schema, validated at startup, and safely synced on upgrade without losing operator overrides
**Depends on**: v1.3.2 (shipped)
**Parallel with**: Phase 7 (coordinate appsettings touch points), Phase 9 (env-var secrets STAB-017 may consume schema work)
**Target release**: v1.4.0
**Requirements**: STAB-007, STAB-008, STAB-009, STAB-010, STAB-011, STAB-012
**Success Criteria** (what must be TRUE):
  1. Generated `appsettings.Production.json` is valid JSON (no inline comments) (gh#22)
  2. An authoritative schema/manifest defines every valid key, type, and default (gh#27)
  3. Pre-flight validation runs at install/startup with actionable errors (gh#25)
  4. Upgrade syncs config against the schema — adds missing keys, flags obsolete keys, never destroys overrides (gh#24)
  5. Upgrade exposes explicit controls (flag/prompt) over sync behavior (gh#26)
  6. Schema-drift check no longer skips when config is otherwise structurally valid (gh#37)
**Plans**: 8 plans
  - [x] 08-01-PLAN.md — Strip template comments + create authoritative JSON Schema (STAB-007, STAB-008)
  - [x] 08-02-PLAN.md — CI Test-Json validation step (STAB-008 enforcement)
  - [x] 08-03-PLAN.md — IValidateOptions<T> validators + Program.cs ValidateOnStart + Event Log fail-fast (STAB-009)
  - [x] 08-04-PLAN.md — Installer pre-flight Test-Json + -ConfigSync param + Event Log source registration (STAB-009, STAB-011)
  - [x] 08-05-PLAN.md — Installer additive-merge sync (schema-driven, arrays atomic, never modify existing) (STAB-010)
  - [x] 08-06-PLAN.md — Installer schema-drift check rewritten (always runs on upgrade) (STAB-012)
  - [x] 08-07-PLAN.md — Publish-PassReset.ps1 ships schema in release zip + pre-publish Test-Json (STAB-008)
  - [x] 08-08-PLAN.md — Operator docs + CHANGELOG (STAB-007..012)

### Phase 9: Security Hardening
**Goal**: Production deployments resist account enumeration, enforce rate-limit + reCAPTCHA, ship structured audit events, and route credentials through env vars instead of plaintext config
**Depends on**: v1.3.2 (shipped)
**Parallel with**: Phase 8 (env-var secrets STAB-017 builds on schema work), Phase 10 (audit events feed /health and CI checks)
**Target release**: v1.4.0
**Requirements**: STAB-013, STAB-014, STAB-015, STAB-016, STAB-017
**Success Criteria** (what must be TRUE):
  1. Production responses do not distinguish `InvalidCredentials` from `UserNotFound` (SIEM still does) (gh#28)
  2. Rate limiting + reCAPTCHA enforcement on `POST /api/password` is covered by tests (both enabled and disabled paths) (gh#29)
  3. Structured audit events cover attempts, failures, rate-limit blocks, successes — with strict secret redaction (gh#30)
  4. HTTPS-first behavior: HTTP→HTTPS redirect, correct HSTS, no accidental plain-HTTP IIS bindings (gh#32)
  5. SMTP/LDAP/reCAPTCHA secrets can be sourced from env vars (or .NET user-secrets in dev) instead of plaintext (gh#33) — stepping stone to V2-003
**Plans**: 5 plans
  - [ ] 09-01-PLAN.md — STAB-013 generic error mapping (IHostEnvironment gate + Production collapse tests)
  - [ ] 09-02-PLAN.md — STAB-014 rate-limit + reCAPTCHA integration tests (4 scenarios via WebApplicationFactory)
  - [ ] 09-03-PLAN.md — STAB-015 structured audit events (AuditEvent DTO + RFC 5424 SD-ELEMENT + SdId config)
  - [ ] 09-04-PLAN.md — STAB-016 HSTS regression test + installer Test-HttpsBinding helper
  - [ ] 09-05-PLAN.md — STAB-017 env-var secrets (binding precedence test + 6 doc files + CHANGELOG)

### Phase 10: Operational Readiness
**Goal**: Operators can verify a deployment is healthy from `/health` alone, installs self-verify before declaring success, CI catches dependency vulnerabilities, and users see effective password policy
**Depends on**: Phases 7, 8 (install + config must be sane before health/CI checks land on top)
**Parallel with**: Phase 9 (final phase of v1.4.0; integrates audit + health surfaces)
**Target release**: v1.4.0
**Requirements**: STAB-018, STAB-019, STAB-020, STAB-021
**Success Criteria** (what must be TRUE):
  1. `/api/health` reports per-dependency readiness (AD, SMTP, expiry background service) without leaking secrets (gh#31)
  2. `Install-PassReset.ps1` post-deploy step calls `/api/health` and `GET /api/password`, fails install on bad response (gh#34)
  3. CI runs `npm audit` + `dotnet list package --vulnerable` on every push/PR; fails on high-severity findings (gh#35)
  4. Effective AD password policy (or clear summary) is displayed in the UI before the user attempts a change (gh#38)
**Plans**: 4 plans (sequential inline execution per D-20)
  - [x] 10-01-PLAN.md — STAB-018 /api/health enrichment (nested AD/SMTP/ExpiryService checks + ConnectAsync timeouts) ✓ 2026-04-20
  - [x] 10-02-PLAN.md — STAB-019 Installer post-deploy /api/health + /api/password verification + -SkipHealthCheck ✓ 2026-04-20 (Tasks 1+2; Task 3 operator UAT deferred)
  - [x] 10-03-PLAN.md — STAB-020 CI security-audit job (npm audit + dotnet --vulnerable) + allowlist ✓ 2026-04-20
  - [x] 10-04-PLAN.md — STAB-021 password policy panel visible above Username by default ✓ 2026-04-20

### Phase 11: v2.0 Multi-OS PoC
**Goal**: A documented, evidence-backed decision on cross-platform viability, validated by a working Docker PoC against a test AD
**Depends on**: v1.4.0 (shipped) — must complete stabilization first
**Parallel with**: None (findings gate Phases 12 and 13 design)
**Target release**: v2.0.0 (research deliverable; production migration may be deferred)
**Requirements**: V2-001
**Success Criteria** (what must be TRUE):
  1. A research document exists comparing `Novell.Directory.Ldap.NETStandard` (and alternatives) against the current `System.DirectoryServices.AccountManagement` usage, with a recommended path
  2. A Docker image builds from the repo and performs a successful password change against a test AD without `S.DS.AM`
  3. An explicit go/no-go decision on full Linux support is captured in `PROJECT.md` Key Decisions
  4. A provider abstraction boundary is identified (or confirmed sufficient) such that future cross-platform work doesn't require a rewrite
**Plans**: Executed via superpowers subagent-driven-development (not enumerated here) — ✅ shipped in v2.0

### Phase 12: v2.0 Local Password DB
**Goal**: Operators can enforce banned-word and attempted-pwned rules locally, independent of (and stricter than) AD policy
**Depends on**: Phase 11 (provider-abstraction findings inform integration shape)
**Parallel with**: Phase 13 (could overlap once Phase 11 lands, but coarse granularity keeps them sequential by default)
**Target release**: v2.0.0
**Requirements**: V2-002
**Success Criteria** (what must be TRUE):
  1. Operators can add and remove banned terms via a documented mechanism; changes take effect without code rebuild
  2. A local attempted-pwned lookup store exists and is consulted during password change; matches reject the change with a distinct `ApiErrorCode`
  3. Local rules are enforced even when AD would accept the password (strictly additive)
  4. Any borrowed logic (e.g., from lithnet/ad-password-protection) has a LICENSE-compatible integration documented in the repo
**Plans**: Executed via superpowers subagent-driven-development (not enumerated here) — ✅ shipped in v2.0

### Phase 13: v2.0 Secure Config Storage
**Goal**: Secrets in `appsettings.Production.json` are never stored as cleartext on disk by default, with a clear upgrade path for existing installs
**Depends on**: Phase 11 (cross-platform constraints shape mechanism choice — e.g., DPAPI is Windows-only), STAB-017 (env-var foundation from v1.4.0)
**Parallel with**: Phase 12 (independent of V2-002 scope)
**Target release**: v2.0.0
**Requirements**: V2-003
**Success Criteria** (what must be TRUE):
  1. SMTP, reCAPTCHA, and LDAP credentials can be stored via a secure mechanism (DPAPI / ASP.NET Core Data Protection / Credential Manager / optional Key Vault adapter) chosen and documented
  2. A fresh install has no cleartext secrets on disk by default
  3. An existing v1.4.x install can upgrade to v2.0 and migrate its secrets following a documented procedure in `UPGRADING.md`
  4. `docs/Secret-Management.md` reflects the new default and documents fallback/override knobs
**Plans**: Executed via superpowers subagent-driven-development (not enumerated here) — ✅ shipped in v2.0

### Phase 14: v2.0 Web Admin Configuration UI
**Goal**: Operators can view and edit `appsettings.Production.json` via a localhost-only web UI that validates against the authoritative schema and prompts for AppPool recycle on write
**Depends on**: Phase 8 (authoritative schema), Phase 13 (secrets segregation — editor must know which keys are secret-managed vs. file-backed)
**Parallel with**: None (sequenced after Phase 13)
**Target release**: v2.0.0
**Requirements**: Admin-UI surface delivered in Phase 13 (loopback admin website); hosting modes delivered in Phase 14
**Success Criteria** (what must be TRUE):
  1. Admin page is reachable only from `127.0.0.1` / `::1` — remote requests return 403
  2. UI renders fields from `appsettings.schema.json` (Phase 08), including types, defaults, and obsolete markers
  3. Changes are validated against the schema client-side AND server-side before write
  4. Save operation writes atomically (tmp file + rename) and triggers an AppPool recycle prompt
  5. Secret-managed keys (Phase 13 outputs) are shown as "managed externally" and not editable via the UI
  6. All writes emit a SIEM audit event (reuses Phase 09 AuditEvent infrastructure)
**Plans**: Executed via superpowers subagent-driven-development (not enumerated here) — ✅ shipped in v2.0

## Cross-Phase Dependencies

| From | To | Nature |
|---|---|---|
| Phase 7 | Phase 10 | Installer post-deploy check (STAB-019) requires sane installer (STAB-001..006) first |
| Phase 8 | Phase 9 | Env-var secrets (STAB-017) consumes schema/manifest (STAB-008) |
| Phase 8 | Phase 10 | Pre-flight validation (STAB-009) feeds /health readiness (STAB-018) |
| Phase 9 | Phase 10 | Audit events (STAB-015) inform /health and CI security gates |
| v1.4.0 | Phase 11 | Stabilization must ship before v2.0 PoC begins |
| Phase 11 | Phase 12 | Provider-abstraction decision informs local-DB integration point |
| Phase 11 | Phase 13 | Platform decision (Windows-only vs cross-platform) constrains secret-storage mechanism |
| STAB-017 | Phase 13 | Env-var secrets is the stepping stone to full secure config storage |
| Phase 8 | Phase 14 | Admin UI renders fields from authoritative appsettings.schema.json |
| Phase 9 | Phase 14 | Admin UI writes emit SIEM AuditEvent via existing audit infrastructure |
| Phase 13 | Phase 14 | Admin UI must know which keys are secret-managed (non-editable) vs. file-backed |

## Parallelism Map

**v1.4.0 (shipped):**
- Phases 7, 8, 9 ran in parallel (different surfaces: installer, config, security middleware)
- Phase 10 sequenced after 7+8+9 landed (integrated their surfaces into /health, post-deploy check, CI)

**v2.0.0 (shipped):**
- Executed sequentially: Phase 11 → Phase 12 → Phase 13 → Phase 14
- Each phase shipped incrementally through the 2.0.0-alpha.1…alpha.8 chain; consolidated into a single `[2.0.0]` release and cut as GA v2.0.1 on 2026-06-01

## Progress

| Phase | Milestone | Status | Completed |
|---|---|---|---|
| 7. Installer & Deployment Fixes | v1.4.0 | ✅ Complete | 2026-04-16 |
| 8. Configuration Schema & Sync | v1.4.0 | ✅ Complete | 2026-04 |
| 9. Security Hardening | v1.4.0 | ✅ Complete | 2026-04 |
| 10. Operational Readiness | v1.4.0 | ✅ Complete | 2026-04-20 |
| 11. Cross-platform LDAP provider | v2.0.0 | ✅ Shipped | 2026-04-21 (alpha.1) |
| 12. Local Password DB | v2.0.0 | ✅ Shipped | 2026-04 |
| 13. Secure Config Storage + Admin UI | v2.0.0 | ✅ Shipped | 2026-04 |
| 14. Pluggable Windows hosting modes | v2.0.0 | ✅ Shipped | 2026-04-24 |

**Production release:** v2.0.1 (GA) published 2026-06-01.

## Coverage

- v1.4.0 requirements: **21** (STAB-001..021) — all delivered ✓
- v2.0.0 requirements: **3** (V2-001, V2-002, V2-003) — all delivered ✓
- Mapped: **24/24** ✓
- Orphans: **0**

---
*Last updated: 2026-06-01 (v2.0.0 shipped; all phases 11–14 complete; GA cut as v2.0.1)*
