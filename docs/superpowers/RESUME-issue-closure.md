# Resume: v1.4.0 Issue-Closure Work

**Last session:** 2026-06-01. **To resume, say:** "continue issue closure" (or "start Plan 2").

## Where things stand

Driving the 16 partial v1.4.0 GitHub issues to full closure via 4 subsystem plans, executed subagent-driven (TDD, per-task commits) on branch **`feat/close-v1.4.0-schema-sync`** (18 commits, pushed, working tree clean).

### ✅ Plan 1 — config-schema-and-sync — COMPLETE
Plan: [`docs/superpowers/plans/2026-06-01-config-schema-and-sync.md`](plans/2026-06-01-config-schema-and-sync.md)
- **#27 (STAB-008), #24 (STAB-010), #26 (STAB-011) — all CLOSED.**
- Final: schema-coverage 9/0, installer Pester suite 30/0, template validates.
- **Key finding:** config sync was *completely non-functional* (3 `Set-StrictMode` landmines in `Get-SchemaKeyManifest`/`Set-LiveValueAtPath`/`Remove-LiveValueAtPath` + silent skip of missing-parent sections) — not just "missing dry-run/backup" as the audit said. All fixed with regression tests. Then added: Diff dry-run, per-file backups, durable sync logs, all-hosting-mode sync (de-gated from IIS), `-ConfigSync` help+ValidateSet (also fixed pre-existing broken comment-based help), operator docs, CI gating.

### ⏳ Remaining — Plans 2, 3, 4 (NOT started) — 13 issues open
Execute in this order (dependency-driven):

1. **Plan 2 — security/backend** [`plans/2026-06-01-security-backend-closure.md`](plans/2026-06-01-security-backend-closure.md) — #28, #29, #30, #32, #33, #36, #38 (~37 tasks). Likely real defects (audit confirmed): **#30 AuditEvent built but never wired into production**; **#28 lockout-code enumeration oracle**; **#38 LDAP provider shows wrong AD policy**. #36/#38 depend on #27 (done).
2. **Plan 3 — installer/PowerShell** [`plans/2026-06-01-installer-powershell-closure.md`](plans/2026-06-01-installer-powershell-closure.md) — #19, #20, #21, #34, #39 (~29 tasks). **#34 is HIGH-risk** (post-deploy verification) and depends on #31 + #32. Put it last.
3. **Plan 4 — health/ops** [`plans/2026-06-01-health-ops-closure.md`](plans/2026-06-01-health-ops-closure.md) — #31 (~10 tasks). Coordinates with installer #34.

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

## Open issues (13): #19 #20 #21 #28 #29 #30 #31 #32 #33 #34 #36 #38 #39
## Closed this effort: #22 #23 #25 #35 #37 (audit) + #27 #24 #26 (Plan 1)
