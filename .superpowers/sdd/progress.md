# Deepen the Password Change Flow — SDD progress ledger

Plan: docs/superpowers/plans/2026-06-20-deepen-password-change-flow.md
Branch: refactor/deepen-change-flow
Branch base (merge-base with master): c6c17b9
Design commit: ea10fa2

Verification policy (project lesson, recorded in memory feedback_verify_inline_not_nested_agents):
run dotnet/pwsh verification INLINE; do NOT dispatch nested review-agents — they
stall under context-mode hooks. Dispatch IMPLEMENTER subagents per task; the
controller adjudicates spec + quality reviews INLINE (build + both test suites).

Safety net: 11 integration tests in
src/PassReset.Tests.Windows/Web/Controllers/GenericErrorMappingTests.cs +
StatusEndpointTests.cs must stay green at every task (behavior preservation).

## Tasks
- Task 1: relocate IRecaptchaVerifier to Common — complete (commit bd21506, review clean INLINE; spec ✅ quality ✅; git-detected rename 96%, only namespace changed + 1 using in GoogleRecaptchaVerifier + FakeRecaptchaVerifier using; Windows suite 253 pass/5 skip/0 fail)
- Task 2: relocate ISiemService + AuditEvent to Common — complete (commit 46436ee, review clean INLINE; spec ✅ quality ✅; both files git-renames, enum 12 members verbatim, SiemService impl stayed in Web. using-fixups: SiemService, Program/Expiry/Health (via existing imports), SiemSyslogFormatter+Validator, 4 test files. RateLimitAndRecaptchaTests: fully-qualified refs rewritten Web.Services.*->Common.* (7/7 churn, no logic/assert change). Windows suite 253 pass/5 skip/0 fail)
- Task 3: add IErrorRedactor seam (TDD) — complete (commit 4cbdbb3, review clean INLINE; spec ✅ quality ✅; 3 files +94 lines, interface+adapter verbatim to brief, static interface member correct. ErrorRedactorTests 8/8 pass — re-verified INLINE by controller. Full solution builds. NOT wired to DI (Task 6).)
- Task 4: Change Flow types + interfaces — complete (commit 1ce4c0a, review clean INLINE; spec ✅ quality ✅; 4 files +93 lines verbatim. All Task-5-facing signatures verified: factories Success/Validation/Captcha/Changed, Disposition{Ok,ValidationError,CaptchaRejected,ChangeFailed}, NotificationRequest(Username,Timestamp,ClientIp), RequestContext(ClientIp,TraceId), ChangePasswordRequest+Recaptcha init, IChangeFlowSettings 4 members, HandleAsync. Common builds 0 warn.)
- Task 5: implement ChangePasswordFlow (TDD) — complete (commit 579beff, review clean INLINE; spec ✅ quality ✅; impl verbatim to brief, 8/8 tests re-verified INLINE. Line-by-line behavior match vs original PostAsync confirmed: AttemptStarted entry, distance-reject emits NO SIEM, RecaptchaFailed, Failed:{code}+MapErrorCodeToSiemEvent+message detail+Redact-via-seam, PasswordChanged success, notify gated, timestamp "u". Deep module: HandleAsync hides full sequence.)
- Task 6: wire DI + rewrite PostAsync thin — complete (commit c94843c, review clean INLINE; spec ✅ quality ✅; PostAsync ~85 lines->thin adapter, StatusAsync+4 private helpers UNTOUCHED, ChangeFlowSettingsAdapter recaptcha guard exact, FireNotification off-path. GATE re-verified INLINE: Windows 253 pass/5 skip/0 fail (all 11 GenericErrorMappingTests + StatusEndpointTests), cross-platform 151 pass/0 fail. ACCEPTED DELTA: AttemptStarted moved into flow -> no longer emitted on ModelState-invalid path (user decision: AttemptStarted=real attempt; document in T7). No test regressed.)
- Task 7: docs pass — complete (commit b8e705d, review clean INLINE; spec ✅ quality ✅; AttemptStarted semantic recorded in CONTEXT.md Change Flow term. NOTE: CLAUDE.md is gitignored (developer-local) so the planned CLAUDE.md edit is local-only; the committed/tracked home is CONTEXT.md. Final build 0 err; cross-platform 151/0; Windows 253/5/0.)

## Minor findings (running)
- T6 ACCEPTED (user): PasswordChangeAttemptStarted no longer fires for ModelState-invalid requests (moved into flow, which runs post-ModelState). Semantic accepted; Task 7 documents it. Not a regression vs any test.
- T5: original controller Audit() also wrote _logger.LogInformation per outcome; the flow emits only the SIEM AuditEvent (no LogInformation). Telemetry-only, no test/wire dependency. Final review: confirm info-log parity is acceptable or re-add at controller boundary.


## Final whole-branch review — DONE, ready to finish (INLINE per project lesson)
- Reviewed c6c17b9..HEAD inline (nested review-agents stall under context-mode hooks).
- No Critical/Important. Behavior preserved: Windows 253/5/0 (all 11 STAB-013 GenericErrorMappingTests + StatusEndpointTests), cross-platform 151/0/0. Build 0 err (80 pre-existing warnings, not from this branch).
- ChangePasswordFlow is a deep module: HandleAsync hides distance/recaptcha/change/redaction/audit; controller is a thin adapter; IErrorRedactor + relocated IRecaptchaVerifier/ISiemService give the flow cross-platform testability (8 + 8 new unit tests on the Linux leg).
- Final-review fix applied (commit 42c8025): removed dead IPasswordChanger _changer dependency from PasswordController (flow owns the seam now). Re-verified both suites green.
- Minor findings carried (non-blocking, recorded above): T5 info-log parity (telemetry only), T6 AttemptStarted semantic (ACCEPTED by user + documented in CONTEXT.md).
- Branch commits: ea10fa2(design) bd21506 46436ee 4cbdbb3 1ce4c0a 579beff c94843c b8e705d 42c8025

---

# Phase 12 — Local Password DB — COMPLETE (closeout recorded 2026-06-22)

Closeout plan: docs/superpowers/plans/2026-06-22-phase-12-closeout.md
(supersedes the obsolete 2026-04-21 plan/spec, deleted in this closeout)

Verified 2026-06-22 (inline per project lesson): all 16 original tasks shipped against the
POST-refactor architecture, not the architecture the 2026-04-21 plan was written for. Three
refactors landed between plan and implementation — carve password-provider seams (2026-06-19),
deepen change flow (2026-06-20), collapse provider wiring (2026-06-20).

Key architecture deltas the original plan missed:
- Seam renamed IPasswordChangeProvider -> IPasswordChanger; decorator implements IPasswordChanger.
- Config also editable via Admin UI (IAppSettingsEditor / LocalPolicySection), not appsettings-only.
- DI: AddSingleton<IPasswordChanger> -> LocalPolicy(Lockout(core)); HIBP disabled flag wired
  when LocalPwnedPasswordsPath set (Program.cs:148-149, 351-369).
- Contract test renamed IPasswordChangeProviderContract -> IPasswordChangerContract;
  LocalBannedWord fact + SeedBannedWord helper preserved on the new seam.

Task status (all ✅): 1 ApiErrorCode 20/21 + FE mirror · 2 LocalPolicyOptions · 3-4 BannedWordsChecker ·
5-6 LocalPwnedPasswordsChecker · 7-8 LocalPolicyPasswordChangeProvider · 9 PwnedPasswordChecker.disabled ·
10 validator fail-fast · 11 contract fact (ported) · 12 Program.cs DI · 13 appsettings defaults ·
14 operator docs (LocalPasswordPolicy-Setup.md + appsettings-Production.md + README + CLAUDE.md) ·
15 CHANGELOG (bundled into v2.0 release section) · 16 regression.

Verification baseline: build 0 warn/0 err; cross-platform PassReset.Tests 151/0/0;
Windows behavior gate PassReset.Tests.Windows 253 pass/5 skip/0 fail.

Closeout bookkeeping (no feature code changed): staged the orphaned old-half rename deletion of
IPasswordChangeProviderContract.cs; deleted the stale 2026-04-21 plan/spec; recorded this entry.
