# Resume: v1.4.0 Issue-Closure Work

**Last session:** 2026-06-01. **To resume, say:** "continue issue closure" (or "start Plan 3").

## Where things stand

Driving the 16 partial v1.4.0 GitHub issues to full closure via 4 subsystem plans, executed subagent-driven (TDD, per-task commits) on branch **`feat/close-v1.4.0-schema-sync`** (~50 commits, pushed, working tree clean except 2 pre-existing untracked planning docs). **13 of 16 closed (Plans 1 & 2 done).**

### ✅ Plan 1 — config-schema-and-sync — COMPLETE
Plan: [`docs/superpowers/plans/2026-06-01-config-schema-and-sync.md`](plans/2026-06-01-config-schema-and-sync.md)
- **#27 (STAB-008), #24 (STAB-010), #26 (STAB-011) — all CLOSED.**
- Final: schema-coverage 9/0, installer Pester suite 30/0 (now 33/0 after #32), template validates.
- **Key finding:** config sync was *completely non-functional* (3 `Set-StrictMode` landmines + silent skip of missing-parent sections) — not just "missing dry-run/backup" as the audit said. All fixed with regression tests, then the planned features added.

### ✅ Plan 2 — security-backend-closure — COMPLETE
Plan: [`docs/superpowers/plans/2026-06-01-security-backend-closure.md`](plans/2026-06-01-security-backend-closure.md)
- **#28, #29, #30, #32, #33, #36, #38 — all CLOSED.**
- Final: backend 342/0 (6 env-skips), frontend 57/0, installer Pester 33/0, lint clean.
- **Real behavioral fixes (audit-confirmed):** #30 structured `AuditEvent` was built but **never wired into production** — now `Audit()` emits structured events with `Activity` TraceId + `PasswordChangeAttemptStarted` anchor; #36 `UnauthorizedAccessException`-wrapped E_ACCESSDENIED was falling through to `Generic` — now mapped to `PasswordTooRecentlyChanged`; #38 LDAP provider hardcoded complexity=false/history=0 — now reads from domain root (degrades gracefully). #28/#29/#32/#33 were coverage/testability/doc gaps — closed with tests, the reCAPTCHA DI seam, installer binding validation, corrected docs.

### ⏳ Remaining — Plans 3 & 4 — 6 issues open (#19, #20, #21, #31, #34, #39)
Execute in this order (dependency-driven):

1. **Plan 4 — health/ops** [`plans/2026-06-01-health-ops-closure.md`](plans/2026-06-01-health-ops-closure.md) — **#31** (~10 tasks). DO THIS BEFORE Plan 3's #34. Core gap: `/api/health` returns 503 on fresh deploy when the expiry service is enabled (degraded), which breaks the installer post-deploy check (#34). Add a config toggle / treat not-yet-run expiry as non-fatal.
2. **Plan 3 — installer/PowerShell** [`plans/2026-06-01-installer-powershell-closure.md`](plans/2026-06-01-installer-powershell-closure.md) — #19, #20, #21, #34, #39 (~29 tasks). **#34 is HIGH-risk** (post-deploy verification) and depends on #31 (Plan 4) + #32 (done). Put #34 LAST. #21 depends on #19.

Audit evidence for all 16: [`issue-vs-code-audit-2026-06-01.md`](issue-vs-code-audit-2026-06-01.md).

## Execution method (what worked)
- Subagent-driven: one implementer subagent per task (TDD: failing test → minimal impl → commit).
- **Run verification DIRECTLY (inline `pwsh`/`gh`), NOT via delegated review agents** — nested review-agents + context-mode hooks stalled. Inline adversarial checks (parse/tokenize, suite, criterion-by-criterion) worked.
- Expect the audit's "partial" labels to understate the work — verify each gap against current code before trusting it.
- Close each issue with a code-cited comment only after its acceptance criteria verify.
- Push the branch after each issue closes.

## When all 4 plans done
- Update `.planning/` (STATE/ROADMAP/PROJECT/REQUIREMENTS) to mark the now-closed STABs complete.
- Use `superpowers:finishing-a-development-branch` to merge `feat/close-v1.4.0-schema-sync` → master (PR or direct).
- Consider a v2.0.2 patch tag if these fixes should ship.

## Open issues (6): #19 #20 #21 #31 #34 #39
## Closed this effort: #22 #23 #25 #35 #37 (audit) + #27 #24 #26 (Plan 1) + #28 #29 #30 #32 #33 #36 #38 (Plan 2)
