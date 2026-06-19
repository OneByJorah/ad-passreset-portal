# Carve IPasswordChangeProvider into Cohesive Seams — SDD progress ledger

Plan: docs/superpowers/plans/2026-06-19-carve-password-provider-seams.md
Branch: refactor/carve-password-provider-seams
Branch base (merge-base with master): ef218af

Verification policy (project lesson): run dotnet/pwsh verification INLINE; do NOT
dispatch nested review-agents — they stall under context-mode hooks. Implementer
subagents only; controller adjudicates reviews inline.

## Tasks
- Task 1: 3 seam interfaces + PasswordDistance free fn — complete (commit 77a0f61, review clean; spec ✅ quality Approved; 6/6 PasswordDistanceTests in PassReset.Tests, old interface untouched)
- Task 2: retarget adapters/decorators, delete old interface — complete (commit c2e5ceb, review clean; spec ✅ quality Approved; both target builds 0err/0warn; bodies untouched, exactly the forwarding methods deleted. DEVIATION: PasswordPolicyCache lives in PassReset.PasswordProvider not Web — migrated to IPasswordStatusReader HERE (build-necessary, correct). So Task 3 Step 1 is already DONE.)
- Task 3: retarget consumers + DI rewire + cref cleanup — complete (commit afffcc9, review clean; spec ✅ quality Approved; production grep CLEAN, PassReset.Web 0 err. Single-instance invariant holds: ResolveAdapter returns the singleton concrete shared by all 3 seams; only change seam decorated. 5 crefs fixed. ASP0000 warning is pre-existing.)
- Task 4: retarget tests + full-suite proof — complete (commit 6a7cc06; controller-verified INLINE: grep CLEAN across all src, full sln build 0err, full suite 375 passed/0 failed/6 skipped — PassReset.Tests 135 (=129+6 new PasswordDistanceTests), Windows 240+5skip, Ldap 1skip. Contract base renamed IPasswordChangerContract. Behavior preserved.)

## Minor findings (running)
- T4: IPasswordChangerContract.cs:42 dangling <see cref="Sut"/> doc comment (pre-existing copy-paste, inert).
- T4: IPasswordChangerContract has I-prefix but is abstract class (pre-existing naming, inherited from old name).
- T3: ProviderMode.cs cref now says IPasswordChanger but mode selects the adapter implementing all 3 seams — accuracy nicety, not a defect (old text was equally narrow). Triage at final review.
- T1: PasswordDistance params named currentPassword/newPassword vs brief's a/b — kept (domain names are clearer, no behavior impact).
- NOTE for T4: THREE test projects exist — PassReset.Tests (net10.0), PassReset.Tests.Windows (net10.0-windows), PassReset.Tests.Integration.Ldap. Full-suite proof must run the SOLUTION (dotnet test src/PassReset.sln), not one project.


## Final whole-branch review — DONE, ready to merge (opus)
- No Critical/Important. Single-instance invariant verified correct in all 3 DI branches: status+directory seams resolve the SAME singleton concrete adapter the change-decorator chain wraps. Change-only decoration preserved (wrong-pw Status Check still bypasses lockout — unchanged). Levenshtein extraction byte-identical. Old interface deleted outright, no composed marker. Full suite 375 passed/0 failed.
- Non-blocking follow-ups (optional, not done): (1) reword dangling <see cref="Sut"/> in IPasswordChangerContract.cs:42; (2) optional rename IPasswordChangerContract → PasswordChangerContract (drop I-prefix on abstract class); (3) robustness: ResolveAdapter cast to IDirectoryUserReader would throw if a FUTURE adapter omits that seam — fine today.
