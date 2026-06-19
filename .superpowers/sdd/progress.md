# Extract IRecaptchaVerifier Seam — SDD progress ledger

Plan: docs/superpowers/plans/2026-06-19-recaptcha-verifier-seam.md
Branch: refactor/recaptcha-verifier-seam
Branch base (merge-base with master): 4ccff41

Verification policy (project lesson): run dotnet/pwsh verification INLINE; do NOT
dispatch nested review-agents — they stall under context-mode hooks. Implementer
subagents only; controller adjudicates reviews inline.

## Tasks
- Task 1: IRecaptchaVerifier interface + FakeRecaptchaVerifier — pending
- Task 2: GoogleRecaptchaVerifier (TDD, 7 branches) — pending
- Task 3: wire seam into controller + DI (typed client) — pending
- Task 4: migrate controller recaptcha tests to fake + full-suite proof — pending

## Minor findings (running)

