# Extract IRecaptchaVerifier Seam — SDD progress ledger

Plan: docs/superpowers/plans/2026-06-19-recaptcha-verifier-seam.md
Branch: refactor/recaptcha-verifier-seam
Branch base (merge-base with master): 4ccff41

Verification policy (project lesson): run dotnet/pwsh verification INLINE; do NOT
dispatch nested review-agents — they stall under context-mode hooks. Implementer
subagents only; controller adjudicates reviews inline.

## Tasks
- Task 1: IRecaptchaVerifier interface + FakeRecaptchaVerifier — complete (commit a356976, review clean; spec ✅ quality Approved; 2 files, both builds clean)
- Task 2: GoogleRecaptchaVerifier (TDD, 7 branches) — complete (commits 1d8c93a + ef6df09, review clean; spec ✅ quality Approved; security axes verified inline: fail-open only on non-2xx/HttpRequestException/TaskCanceledException, generic catch unconditionally false, >= threshold, action via parameter. Reviewer's 2 Minor coverage gaps FIXED — added NetworkThrow_FailOpenFalse + 2 Timeout tests; 11/11 pass.)
- Task 3: wire seam into controller + DI (typed client) — complete (commits 59e7d2c + 8534db6, review clean; spec ✅ quality Approved; both call sites use VerifyAsync(model.Recaptcha,"change_password",clientIp) with enable-gate/Audit/InvalidCaptcha intact; ValidateRecaptchaAsync+RecaptchaResponse+IHttpClientFactory+_recaptchaHttp removed; typed client registered. FIX: null-guard added to verifier (fails CLOSED) clearing 3 CS8602 — NullConfig_ReturnsFalse test, 12/12 pass.)
- Task 4: migrate controller recaptcha tests to fake + full-suite proof — complete (commit 6584c72, review clean; spec ✅ quality Approved; all 4 boolean mappings verified correct (no false green); assertions unchanged; named-client test replaced w/ GoogleRecaptchaVerifier type check; RecaptchaEnabledFactory untouched; StubRecaptchaHandler/JsonOk deleted. Full suite INLINE: 387 passed/0 failed/6 skipped (135+252+0; +5+1 skip). +12 vs master = new verifier tests.)

## Minor findings (running)
- T4: RecaptchaVerifier_IsRegistered uses GetService not GetRequiredService (diagnostics nit).
- T4: test name ..._FailSafeEnabled_Returns200 slightly misleading post-seam (fail-open now verifier-level). Naming judgment.
- T3: config.PrivateKey! in verifier relies on controller enable-gate (safe today; a direct call w/o gate would send empty key). Optional: ArgumentException guard or XML-doc note. Non-blocking.
- T3: ctor assignment order cosmetic (reviewer noted, no impact).


## Final whole-branch review — DONE, ready to merge (opus)
- No Critical/Important. Fail-open security invariant intact on every path (open only on non-2xx/HttpRequestException/TaskCanceledException under FailOpenOnUnavailable; generic catch + null-config + empty-PrivateKey all fail CLOSED). Behavior-preserving; deep seam; controller retains gate+audit+response at both call sites; migrated tests faithful (no false green); fail-open matrix has dedicated unit coverage.
- Final-review fixes applied (commit 52a3ff4): (1) empty-PrivateKey guard in verifier + dropped `!` (self-contained seam, fails closed) + EmptyPrivateKey_ReturnsFalse test; (2) RecaptchaVerifier_IsRegistered uses GetRequiredService; (3) renamed Recaptcha_ProviderUnreachable_FailSafeEnabled_Returns200 → Recaptcha_VerifierAllows_Returns200.
- Full solution suite: 388 passed / 0 failed / 6 skipped (135 + 253+5skip + 1skip). +13 vs master's 375 = 13 GoogleRecaptchaVerifier tests. xUnit1051 warnings pre-existing in GenericErrorMappingTests (not from this branch).
