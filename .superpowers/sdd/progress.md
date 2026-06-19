# Carve IPasswordChangeProvider into Cohesive Seams — SDD progress ledger

Plan: docs/superpowers/plans/2026-06-19-carve-password-provider-seams.md
Branch: refactor/carve-password-provider-seams
Branch base (merge-base with master): ef218af

Verification policy (project lesson): run dotnet/pwsh verification INLINE; do NOT
dispatch nested review-agents — they stall under context-mode hooks. Implementer
subagents only; controller adjudicates reviews inline.

## Tasks
- Task 1: 3 seam interfaces + PasswordDistance free fn — pending
- Task 2: retarget adapters/decorators, delete old interface — pending
- Task 3: retarget consumers (controller, expiry svc, policy cache) + DI rewire — pending
- Task 4: retarget tests + full-suite proof — pending

## Minor findings (running)

