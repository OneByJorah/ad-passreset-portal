# Next Phase — Dependabot: undici 7.27.0 → 7.28.0 (test-only transitive)

> **For agentic workers:** small, well-scoped dependency phase. Use
> superpowers:subagent-driven-development or execute inline. The fix is one lockfile bump
> (PR #68 already exists); the real work is confirming the blocked CI failure is unrelated and
> closing both alerts. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Clear the two open Dependabot alerts on `undici` by landing the `7.28.0` bump, after
confirming the existing PR's CI failure is unrelated to the bump.

**Created:** 2026-06-22, from triage during the v2.1.0 release push (GitHub surfaced 1 high + 1
moderate on the default branch).

---

## ⚠️ UPDATE 2026-06-22 — investigated; bump is SAFE, CI red is NOT the bump

PR #68 was rebased onto current master and CI re-run (`run 27939583678`). Two red jobs, **neither
caused by undici 7.28.0** — verified by local reproduction:

1. **`integration-tests-ldap` job: `SAMBA_TEST_OLD_PASSWORD is empty` → exit 1.**
   Environmental: Dependabot PRs don't receive repo **secrets** by default, so the Samba LDAP
   integration job can't run. Affects *any* Dependabot PR; nothing to do with undici. (Almost
   certainly the cause of the original 2026-06-20 failure too.)

2. **`PasswordForm.test.tsx`: 2 failures** — `shows PasswordTooRecentlyChanged message on BUG-002
   error code` couldn't find `/one more failed attempt/i` and `/changed too recently/i`.
   **Flaky, not undici.** Reproduction (local):
   - Baseline undici **7.27.0**: the test **passes**.
   - undici **7.28.0** (forced via `overrides`, lockfile-only): the test **also passes** — full
     `PasswordForm.test.tsx` is **10/10 green** locally on 7.28.0.
   - In CI the failing run took **16.6 s** for that file (vs <1 s local). The test uses async
     `findAllByText` with the default ~1 s timeout; under heavy concurrent CI load (backend +
     frontend + LDAP jobs on one runner) the element rendered after the query gave up. A
     **pre-existing latent flake**, surfaced by CI load — independent of this PR.

**Conclusion:** undici 7.28.0 is safe to land (test-only transitive under jsdom; full frontend
suite green locally on the bumped version). The two red CI jobs are (1) a Dependabot-secrets
limitation and (2) a flaky timing-sensitive test — both orthogonal to the bump.

### Revised next steps
- [ ] **Merge decision (human):** the bump is verified safe; PR #68 can merge once the
      *non-undici* red checks are understood/accepted — admin-merge, or re-run `tests` (the
      PasswordForm flake may pass on retry; the LDAP-secrets job fails for any Dependabot PR by design).
- [ ] **Follow-up (separate, recommended):** de-flake `PasswordForm.test.tsx` BUG-002 test (raise
      the `findAllByText` timeout / await a stable anchor) so CI load can't trip it. The real
      actionable defect this triage uncovered.
- [ ] After merge, confirm alerts #5/#6 auto-close.

> **Correction note:** an earlier pass this session reported the bump "verified safe, just merge"
> after a partial local check that reverted before running the frontend suite. That was
> incomplete. The above is the corrected, fully-reproduced conclusion.

---

## The alerts (both fixed by undici 7.28.0)

| # | Severity | GHSA | Summary |
|---|----------|------|---------|
| 5 | **high** | GHSA-vmh5-mc38-953g | TLS certificate validation bypass via dropped `requestTls` in SOCKS5 `ProxyAgent` (vulnerable `>= 7.23.0, < 7.28.0`) |
| 6 | medium | GHSA-pr7r-676h-xcf6 | Cross-user information disclosure via shared-cache whitespace bypass (vulnerable `>= 7.0.0, < 7.28.0`) |

Both in `src/PassReset.Web/ClientApp/package-lock.json`. Patched version for both: **`7.28.0`**.

## Risk assessment (why this is low-urgency despite a "high")

- **`undici` is a transitive *devDependency*.** Chain: `passcore-web → jsdom@29.1.1 → undici@7.27.0`.
  `jsdom` is the Vitest/RTL test DOM environment — it is **not in the production bundle** and
  never ships to a browser or server.
- Both CVEs require runtime use of undici's HTTP client (SOCKS5 proxy TLS; HTTP cache). Neither
  code path is exercised by jsdom in unit tests. **Real-world exposure is effectively nil.**
- Worth fixing anyway to clear the alerts and keep the tree clean — but it is **not** a
  release blocker and did **not** affect v2.1.0 (which shipped from `742007d`).

## Current state

- **PR #68 is OPEN**: `chore(deps-dev): bump undici from 7.27.0 to 7.28.0` on branch
  `dependabot/npm_and_yarn/src/PassReset.Web/ClientApp/undici-7.28.0`.
- Its CI run (`27870225962`) **failed at the `build` job** (tests `skipped` because build failed).
  The failing job log shows the **PowerShell installer / schema-drift + Pester gates**, NOT the
  npm/undici path — strongly suggesting the failure is **unrelated** to the undici bump (a
  jsdom-only test dep cannot affect the .NET build or the installer suite). **This must be
  confirmed, not assumed, in Step 1.**

---

### Task 1: Confirm PR #68's CI failure is unrelated to the undici bump

- [ ] **Step 1: Pull the precise failing assertion**

  ```bash
  gh run view 27870225962 --log-failed > /tmp/undici-pr-ci.log
  ```
  Read it for the actual `::error::` line(s). Expectation: an installer/schema gate
  (`Schema validation failed…`, `appsettings.schema.json is missing sections…`, or
  `Installer Pester suite failed`), i.e. nothing touching `npm`, `vite`, `tsc`, `jsdom`, or
  `undici`.

- [ ] **Step 2: Decide unrelated vs. real**
  - **If unrelated** (installer/schema gate, as expected): note it; the bump is safe to land.
    Check whether the same gate is now green on `master` (it may have been a transient or
    since-fixed failure) — if `master`'s latest CI is green on that job, the PR just needs a rebase.
  - **If actually caused by the bump** (npm/build path): escalate — this becomes a real
    investigation (does `7.28.0` break jsdom under our Node/Vite? pin or wait for a jsdom bump
    that requires it). Re-scope this plan accordingly. *(Low probability given the dep tree.)*

---

### Task 2: Land the bump

- [ ] **Step 1: Rebase PR #68 on current master** (master moved 15+ commits during v2.1.0).

  ```bash
  gh pr comment 68 --body "@dependabot rebase"
  ```
  (or rebase locally). Wait for CI to re-run on the rebased head.

- [ ] **Step 2: Verify frontend suite green with undici 7.28.0**

  Locally, to de-risk before merge:
  ```bash
  cd src/PassReset.Web/ClientApp && npm ci && npm run build && npm test
  ```
  Expected: build passes, Vitest green (baseline 91 tests). `npm ls undici` should now show
  `undici@7.28.0` under `jsdom`.

- [ ] **Step 3: Merge PR #68** once CI is green on the rebased head.

  ```bash
  gh pr merge 68 --squash --delete-branch
  ```

- [ ] **Step 4: Confirm both alerts auto-close**

  ```bash
  gh api repos/:owner/:repo/dependabot/alerts --jq '.[] | select(.state=="open") | .number'
  ```
  Expected: alerts #5 and #6 no longer listed (GitHub auto-dismisses on the patched version
  reaching the default branch).

---

### Task 3 (only if Task 1 found the failure is real / bump can't land cleanly)

- [ ] Investigate the jsdom→undici interaction under our Node + Vite versions; consider
      `overrides`/`resolutions` to force `undici@7.28.0` only in the test tree, or wait for a
      jsdom release that pulls it. Document the decision in CHANGELOG under `[Unreleased]` →
      `### Security`. Do **not** force a transitive override without confirming the test suite
      stays green.

---

## Self-review

- **Scope is honest:** the fix is one lockfile bump that already exists as PR #68. The plan's
  weight is in *confirming the blocker is unrelated* (Task 1) before merging — not in writing code.
- **No silent assumptions:** Task 1 explicitly verifies the "unrelated CI failure" hypothesis
  rather than asserting it.
- **Severity vs. urgency separated:** documented why a "high" CVE is low real-world risk here
  (test-only transitive, vulnerable code paths unused) — so this is correctly *next phase*, not
  a hotfix that should have blocked v2.1.0.
