# Implementation Plan: Installer & PowerShell Closure (Issues #19, #20, #21, #34, #39)

## Goal

Drive GitHub issues #19 (STAB-001 port binding), #20 (STAB-002 reconfigure messaging), #21 (STAB-006 prerequisite auto-install), #34 (STAB-019 post-deploy health verification), and #39 (STAB-005 CI PowerShell gate) to **full closure** — every acceptance criterion met, every change covered by a Pester or CI test. The work hardens `deploy/Install-PassReset.ps1`, adds a CI PowerShell parse/lint/encoding gate, and adds a Pester suite for `Uninstall-PassReset.ps1`.

## Architecture

`deploy/Install-PassReset.ps1` is a single 1855-line script. It dot-sources cleanly for testing: when `$env:PASSRESET_TEST_MODE -eq '1'` it `return`s at **line 748**, exposing every function defined *above* that line to Pester without running the install flow. **All testable logic must therefore live in a pure function defined before line 748**, and the inline install flow (lines 750+) calls that function. The existing tests (`deploy/Install-PassReset.Tests.ps1`) already follow this pattern with `Test-HostingModeValue` and `Test-ServiceModePreflight`.

Three new pure helper functions will be extracted so banner text, post-deploy diagnostics, host-header resolution, and health-status evaluation become unit-testable:

- `Get-DoneBannerMessage` — returns "installed" / "upgraded" / "reconfigured" string (#20).
- `Resolve-HealthHostHeader` — extracts the host header from an IIS `BindingInformation` string (#34).
- `Test-HealthResponseHealthy` + `Get-HealthFailureDiagnostics` — evaluate aggregate `status` and build the multi-line diagnostic block (#34).
- `Test-PrerequisitesResult` / `Resolve-DependencyAction` — decide prompt/yes/no/abort + reboot-pending for prerequisite handling (#21).

`Uninstall-PassReset.ps1` has **no** test-mode short-circuit; its logic is inline at top level. Rather than rewrite it, the new `Uninstall-PassReset.Tests.ps1` validates it via **syntax tokenization + AST inspection** (parse-check, param block, `-KeepFiles` guard presence) plus a thin extracted helper — this keeps the working uninstaller untouched (low regression risk) while satisfying #39's testability gap.

CI gains a `powershell-quality` job (parse-check + PSScriptAnalyzer + encoding check + Pester) wired into both `ci.yml` and `release.yml` (the latter gates the release package).

## Tech Stack

- **PowerShell 7 / Pester 5** — `deploy/*.Tests.ps1`, run via `pwsh -NoProfile -Command "Invoke-Pester ..."`.
- **PSScriptAnalyzer** — installed in CI via `Install-Module`.
- **GitHub Actions** (`windows-latest`) — new `powershell-quality` job.
- C#/xUnit and TS/Vitest are untouched by this plan.

> **Agentic-worker sub-skill note:** Execute this plan with `superpowers:subagent-driven-development`. Each task is one implementer + spec-review + code-quality loop. Follow `superpowers:test-driven-development` strictly: write the failing test first, watch it fail, write minimal code, watch it pass, commit. Use `superpowers:systematic-debugging` if any test fails unexpectedly — root-cause before patching.

---

## File Structure

| File | Created / Modified | Responsibility (single) |
|------|--------------------|--------------------------|
| `deploy/Install-PassReset.ps1` | Modified | Installer: port-binding fix, banner function, prereq function, health/host-header/diagnostics functions, new params |
| `deploy/Install-PassReset.Tests.ps1` | Modified | Pester unit tests for the new installer helper functions |
| `deploy/Uninstall-PassReset.Tests.ps1` | **Created** | Pester syntax/AST/param tests for the uninstaller |
| `.github/workflows/ci.yml` | Modified | Add `powershell-quality` job (parse + analyzer + encoding + Pester) |
| `.github/workflows/release.yml` | Modified | Gate release on `powershell-quality` job (`needs:`) |
| `docs/IIS-Setup.md` | Modified | Document `-Reconfigure`, `-InstallDependencies`, `-SkipDependencyCheck`, reboot-pending behavior |
| `docs/appsettings-Production.md` | Modified | Correct stale `-Reconfigure` references (now a real param) |
| `.editorconfig` | Verified (no change) | Already enforces `[*.ps1]` UTF-8 + final newline + trim — confirm only |

---

## Prerequisites & Sequencing

- **Execution order across the four-plan effort: this plan is #3** (after the schema/config-sync plan, after the health plan).
- **#19 must land before #21 and #34** (they call `$selectedHttpPort` / the binding the port fix produces). #19 is first below.
- **#34 is HIGH regression risk** and depends on:
  - **#24, #26, #27** (the `/api/health` aggregate-status contract — the `status` field and `checks.{ad,smtp,expiryService}` shape that #34 reads),
  - the **#31 health plan** and **#32 (TLS)** — the health endpoint and HTTPS binding the verifier hits.
  - These are delivered by the **schema/config plan and the health plan that run before this one**. **Do not start #34 tasks until those plans are merged** and `GET /api/health` returns `{ status, checks }`. #34 is therefore sequenced **last** here.
- **#39 depends on #23/#27/#34** only in that the CI gate should run *after* the scripts it lints are in their final shape; its tasks are independent of the others and placed before #34 so the gate exists when #34 lands.

Regression-guard discipline (the codebase explicitly values not breaking working code): every medium/high-risk task below includes a regression-guard test and a "What could break" note.

---

## Issue #19 (STAB-001) — Port Binding / Fresh Install

**Root cause (verified at HEAD):** The site is created on `$selectedHttpPort` (`deploy/Install-PassReset.ps1:1340`), but the HTTP→HTTPS redirect-binding block uses the original `$HttpPort` param at lines **1412, 1424, 1426, 1427, 1429**. When port 80 is occupied and an alternate (e.g. 8080) is selected, the script re-binds port 80 → `0x800700B7` / silent failure. Fix: use `$selectedHttpPort` throughout the HTTP-binding block.

**Risk: medium** — touches live binding logic. Regression guard: a test asserting the existing `-HttpPort 0` (HTTPS-only) removal path still keys off the *site's* port.

### Task 19.1 — Failing test: HTTP redirect binding uses the resolved alternate port

**Files:** `deploy/Install-PassReset.Tests.ps1` (append new `Describe`, after line 50)

- [ ] **Step 1 — Write failing test.** The binding block is inline (below line 748), so we test the *invariant* by static AST inspection: assert the source's HTTP-binding `elseif ($CertThumbprint)` block references `$selectedHttpPort` and contains **no** `$HttpPort` token inside the `New-IISSiteBinding ... -Protocol http` creation. Append to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'Install-PassReset: STAB-001 HTTP redirect binding uses resolved port' {
    BeforeAll {
        $scriptPath = "$PSScriptRoot/Install-PassReset.ps1"
        $tokens = $null; $errs = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            $scriptPath, [ref]$tokens, [ref]$errs)
        # Isolate the HTTP-binding creation statements: the New-IISSiteBinding call
        # for protocol http that retains the redirect binding.
        $httpBindingCalls = $ast.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.CommandAst] -and
            $n.GetCommandName() -eq 'New-IISSiteBinding' -and
            $n.Extent.Text -match '-Protocol http\b'
        }, $true)
        $script:HttpBindingText = ($httpBindingCalls | ForEach-Object { $_.Extent.Text }) -join "`n"
    }

    It 'creates the retained HTTP binding on $selectedHttpPort' {
        $script:HttpBindingText | Should -Match '\$selectedHttpPort'
    }

    It 'does not bind the original $HttpPort param when creating the retained HTTP binding' {
        $script:HttpBindingText | Should -Not -Match '\$HttpPort'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL.**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*STAB-001 HTTP redirect*'"
```
(Fails: current `New-IISSiteBinding` uses `"*:${HttpPort}:"`.)

- [ ] **Step 3 — Minimal implementation.** In `deploy/Install-PassReset.ps1`, edit the `elseif ($CertThumbprint)` block (lines 1420-1431). Replace `${HttpPort}` references in the binding-creation path with `${selectedHttpPort}`:

```powershell
} elseif ($CertThumbprint) {
    # Ensure the HTTP binding exists on the site's *resolved* port so ASP.NET Core
    # UseHttpsRedirection() can receive and redirect plain-HTTP requests.
    # STAB-001: must be $selectedHttpPort (alternate port chosen on port-80 conflict),
    # NOT the original $HttpPort param — otherwise we re-bind an occupied port 80.
    $existingHttp = @(Get-IISSiteBinding -Name $SiteName -Protocol http -ErrorAction SilentlyContinue) |
        Where-Object { $_.BindingInformation -match ":${selectedHttpPort}:" }
    if (-not $existingHttp) {
        New-IISSiteBinding -Name $SiteName -BindingInformation "*:${selectedHttpPort}:" -Protocol http | Out-Null
        Write-Ok "HTTP :$selectedHttpPort binding retained for HTTP→HTTPS redirect"
    } else {
        Write-Ok "HTTP :$selectedHttpPort binding present (HTTP→HTTPS redirect active)"
    }
}
```

> **What could break:** The `-HttpPort 0` removal branch (lines 1412-1419) intentionally still uses `$HttpPort -le 0` as its *guard* — leave that condition untouched; only the creation branch changes. `$selectedHttpPort` is always assigned before this block (line 1256 default, refined at 1299/1305/1322), so it is never `$null` here.

- [ ] **Step 4 — Run, expect PASS.**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*STAB-001 HTTP redirect*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "fix(installer): bind HTTP redirect to resolved alternate port [STAB-001 #19]"
```

### Task 19.2 — Regression guard: HTTPS-only removal path still keys off the site port

**Files:** `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** Assert the `-HttpPort 0` removal branch is still guarded by `$HttpPort -le 0` (it must NOT silently switch to `$selectedHttpPort` for the removal *decision*), preventing a regression where alternate-port installs accidentally strip HTTP:

```powershell
Describe 'Install-PassReset: STAB-001 HTTPS-only removal guard intact' {
    It 'keeps the -HttpPort 0 removal branch guarded by $HttpPort -le 0' {
        $src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw
        $src | Should -Match 'if \(\$CertThumbprint -and \$HttpPort -le 0\)'
    }
}
```

- [ ] **Step 2 — Run, expect PASS immediately** (guard already present — this is a *pinning* test, expected green from the start; it locks the behavior so 19.1 can't regress it):
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*HTTPS-only removal guard*'"
```
> For a pure regression-guard pin where the production code is already correct, Step 3 (implementation) is a no-op; document that the test exists to fail *if a future edit* changes the guard.

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.Tests.ps1 && git commit -m "test(installer): pin HTTPS-only removal guard against STAB-001 regression [#19]"
```

### Task 19.3 — Verify acceptance criteria & close #19

- [ ] Run the full installer suite: `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"` — all green.
- [ ] Confirm against issue #19 acceptance criteria: (1) installer no longer attempts to start on an occupied port — the retained HTTP binding now matches the site's actual port; (2) fresh installs on default IIS succeed when port 80 is occupied and an alternate is chosen.
- [ ] Close on GitHub:
```
gh issue close 19 --reason completed --comment "STAB-001 fixed: HTTP redirect binding now uses \$selectedHttpPort (the resolved alternate port), so alternate-port installs no longer re-bind an occupied port 80. Covered by AST regression tests in deploy/Install-PassReset.Tests.ps1 (STAB-001 HTTP redirect binding + HTTPS-only removal guard). Commits on master."
```

---

## Issue #20 (STAB-002) — Installer UX / Messaging

**Root cause (verified at HEAD):** The Done banner (`deploy/Install-PassReset.ps1:1824-1835`) shows "PassReset upgraded successfully" whenever `$backupPath` is set, but a backup is created for **any** existing install (line 1046-1067), including reconfigure (same-version) runs. The `$isReconfigure` flag (line 988) is ignored by the banner. Docs reference a non-existent `-Reconfigure` parameter (`docs/appsettings-Production.md:52,58,71`).

**Risk: low.** Decision: **add a real `-Reconfigure` switch** (makes existing docs accurate and gives operators an explicit trigger) rather than deleting doc references.

### Task 20.1 — Failing test: banner message function (install / upgrade / reconfigure)

**Files:** `deploy/Install-PassReset.ps1` (add function before line 748), `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Install-PassReset: Get-DoneBannerMessage' {
    It 'reports reconfigured when IsReconfigure is set (even with a backup)' {
        Get-DoneBannerMessage -BackupPath 'C:\inetpub\PassReset_backup_x' -IsReconfigure $true |
            Should -Be 'PassReset reconfigured successfully.'
    }
    It 'reports upgraded when a backup exists and not reconfigure' {
        Get-DoneBannerMessage -BackupPath 'C:\inetpub\PassReset_backup_x' -IsReconfigure $false |
            Should -Be 'PassReset upgraded successfully.'
    }
    It 'reports installed on a fresh install (no backup)' {
        Get-DoneBannerMessage -BackupPath $null -IsReconfigure $false |
            Should -Be 'PassReset installed successfully.'
    }
    It 'reports installed when no backup even if reconfigure flag somehow set' {
        Get-DoneBannerMessage -BackupPath $null -IsReconfigure $true |
            Should -Be 'PassReset installed successfully.'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL** (function undefined):
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Get-DoneBannerMessage*'"
```

- [ ] **Step 3 — Minimal implementation.** In `deploy/Install-PassReset.ps1`, add the function in the Helpers region (after `Test-HostingModeValue`, before line 748 — e.g. after line 431):

```powershell
function Get-DoneBannerMessage {
    <#
        STAB-002: choose the Done-banner verb. A backup is created for ANY existing
        install, so $BackupPath alone cannot distinguish upgrade from reconfigure.
        Reconfigure (same incoming version) only counts when an existing install
        was present (i.e. a backup exists).
    #>
    param(
        [string]  $BackupPath,
        [bool]    $IsReconfigure
    )
    if (-not $BackupPath)            { return 'PassReset installed successfully.' }
    if ($IsReconfigure)             { return 'PassReset reconfigured successfully.' }
    return 'PassReset upgraded successfully.'
}
```

- [ ] **Step 4 — Run, expect PASS.**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Get-DoneBannerMessage*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add Get-DoneBannerMessage for reconfigure-aware banner [STAB-002 #20]"
```

### Task 20.2 — Wire the banner function into the Done block

**Files:** `deploy/Install-PassReset.ps1:1824-1835`

- [ ] **Step 1 — Write failing test.** Append an AST/source test asserting the Done block calls the function and no longer hard-codes the unconditional "upgraded" string under a bare `if ($backupPath)`:

```powershell
Describe 'Install-PassReset: Done banner wiring' {
    It 'Done block calls Get-DoneBannerMessage' {
        $src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw
        $src | Should -Match 'Get-DoneBannerMessage -BackupPath \$backupPath -IsReconfigure \$isReconfigure'
    }
    It 'no longer prints an unconditional upgraded banner inside a bare if ($backupPath)' {
        $src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw
        # The literal hard-coded upgraded-success Write-Host must be gone.
        $src | Should -Not -Match "Write-Host '  PassReset upgraded successfully\.'"
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Done banner wiring*'"
```

- [ ] **Step 3 — Minimal implementation.** Replace lines 1824-1835:

```powershell
Write-Host ''
Write-Host '======================================================' -ForegroundColor Cyan
$bannerMessage = Get-DoneBannerMessage -BackupPath $backupPath -IsReconfigure $isReconfigure
Write-Host "  $bannerMessage" -ForegroundColor Green
if ($backupPath -and -not $isReconfigure) {
    Write-Host ''
    Write-Host '  Backup of previous installation:' -ForegroundColor Yellow
    Write-Host "    $backupPath"                    -ForegroundColor Yellow
    Write-Host '  To roll back manually: stop the site, robocopy the backup'
    Write-Host '  folder back to $PhysicalPath, then start the site.'
} elseif ($backupPath -and $isReconfigure) {
    Write-Host ''
    Write-Host '  Reconfigure mode: files were not mirrored; existing deployment preserved.' -ForegroundColor Yellow
}
```

> **What could break:** `$isReconfigure` is `$false`-initialized at line 972 before any existing-install detection, so strict-mode on fresh installs is safe. The backup note now only shows for true upgrades — verify the rollback wording is unchanged for the upgrade path.

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Done banner wiring*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "fix(installer): show reconfigured banner in reconfigure mode [STAB-002 #20]"
```

### Task 20.3 — Add the `-Reconfigure` switch parameter + force-flag wiring

**Files:** `deploy/Install-PassReset.ps1:85-121` (param block), `deploy/Install-PassReset.ps1:972` (flag seed)

- [ ] **Step 1 — Write failing test.** Append (uses `Get-Command` against the dot-sourced script — but the script returns before exposing params; instead inspect the AST param block):

```powershell
Describe 'Install-PassReset: -Reconfigure parameter' {
    BeforeAll {
        $tokens = $null; $errs = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            "$PSScriptRoot/Install-PassReset.ps1", [ref]$tokens, [ref]$errs)
        $script:ParamNames = $ast.ParamBlock.Parameters.Name.VariablePath.UserPath
    }
    It 'declares a -Reconfigure switch parameter' {
        $script:ParamNames | Should -Contain 'Reconfigure'
    }
    It 'help text documents -Reconfigure' {
        (Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw) |
            Should -Match '\.PARAMETER Reconfigure'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*-Reconfigure parameter*'"
```

- [ ] **Step 3 — Minimal implementation.** (a) Add help text in the comment block (after the `.PARAMETER Force` block, around line 71):

```powershell
.PARAMETER Reconfigure
    Force reconfigure mode: re-run app-pool / binding / config logic without mirroring
    files, even when the incoming version differs. Same-version re-runs auto-detect
    reconfigure; this switch makes it explicit (e.g. to re-apply config after editing).
```

(b) Add the param to the block (after `[switch] $Force,` at line 105):

```powershell
    [switch] $Force,

    # STAB-002: explicit reconfigure trigger (same-version re-runs auto-detect this).
    [switch] $Reconfigure,
```

(c) Seed `$isReconfigure` from the switch at line 972 so an explicit `-Reconfigure` forces the mode:

```powershell
$isReconfigure = [bool]$Reconfigure
```

> **What could break:** Line 972 previously set `$isReconfigure = $false`. Defaulting to `[bool]$Reconfigure` ($false when the switch is absent) preserves existing behavior exactly; the version-comparison block (line 988) can still flip it to `$true`. The file-mirror skip at line 1075 (`if (-not $isReconfigure)`) now also respects an explicit `-Reconfigure` — intended.

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*-Reconfigure parameter*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add explicit -Reconfigure switch [STAB-002 #20]"
```

### Task 20.4 — Update operator docs for reconfigure behavior

**Files:** `docs/IIS-Setup.md:236-259` (Step 6), `docs/appsettings-Production.md:52,58,71`

- [ ] **Step 1 — Write failing test.** Append a docs-consistency test to the Pester suite:

```powershell
Describe 'Docs: reconfigure parameter is real and documented' {
    It 'IIS-Setup.md documents reconfigure re-runs' {
        (Get-Content "$PSScriptRoot/../docs/IIS-Setup.md" -Raw) |
            Should -Match '(?i)reconfigure'
    }
    It 'appsettings-Production.md -Reconfigure references match a real param' {
        $ast = $null; $t = $null; $e = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            "$PSScriptRoot/Install-PassReset.ps1", [ref]$t, [ref]$e)
        $names = $ast.ParamBlock.Parameters.Name.VariablePath.UserPath
        $docMentions = (Get-Content "$PSScriptRoot/../docs/appsettings-Production.md" -Raw) -match '-Reconfigure'
        # If the docs mention -Reconfigure, the param must exist.
        if ($docMentions) { $names | Should -Contain 'Reconfigure' }
    }
}
```

- [ ] **Step 2 — Run, expect PASS** (the IIS-Setup half fails until Step 3 if "reconfigure" is absent — run and confirm):
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*reconfigure parameter is real*'"
```

- [ ] **Step 3 — Minimal implementation.** In `docs/IIS-Setup.md`, after the upgrade example (line 258-259), add:

```markdown
# Re-configure an existing install (re-apply binding/config without mirroring files)
# Same-version re-runs auto-detect reconfigure mode; -Reconfigure forces it explicitly:
.\deploy\Install-PassReset.ps1 -Reconfigure -Force -CertThumbprint "PASTE_THUMBPRINT_HERE"
```

The stale `-Reconfigure` references in `docs/appsettings-Production.md` (lines 52, 58, 71) are now correct because the param exists — leave them, the test pins them to a real param.

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*reconfigure parameter is real*'"
```

- [ ] **Step 5 — Commit.**
```
git add docs/IIS-Setup.md deploy/Install-PassReset.Tests.ps1 && git commit -m "docs(installer): document -Reconfigure and same-version re-run behavior [STAB-002 #20]"
```

### Task 20.5 — Verify acceptance criteria & close #20

- [ ] Full suite green: `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"`.
- [ ] Manual smoke (optional, on a test IIS host): install v1.0.0, re-run same v1.0.0 → banner reads "reconfigured successfully" and log shows "Reconfigure mode - skipping file mirror".
- [ ] Confirm all four #20 criteria: reconfigure banner wording, no misleading upgrade messaging, documented behavior, logs indicate reconfigure mode.
- [ ] Close:
```
gh issue close 20 --reason completed --comment "STAB-002 fixed: Done banner now says installed/upgraded/reconfigured via Get-DoneBannerMessage; added a real -Reconfigure switch; docs updated (IIS-Setup.md, appsettings-Production.md). Unit-tested in deploy/Install-PassReset.Tests.ps1."
```

---

## Issue #21 (STAB-006) — Installer Prerequisites (auto-install / re-check / non-interactive)

**Depends on #19** (lands first). **Root cause (verified at HEAD):** IIS-feature install (lines 837-868) has an interactive prompt but **no post-DISM re-check** and **no reboot-pending stop** (DISM exit 3010 is treated as success and the script proceeds to site creation). The .NET Hosting Bundle block (lines 876-894) only `exit 0`s with manual instructions — no auto-install option, no structured "missing" list, no `-InstallDependencies` / `-SkipDependencyCheck` flags for unattended use.

**Risk: medium** — adds control flow to the prerequisite phase that gates site creation. Regression guard: existing default behavior (interactive prompt, `-Force` → DISM) must be unchanged when the new flags are absent.

### Task 21.1 — Failing test: dependency-action resolver (prompt/yes/no + Force)

**Files:** `deploy/Install-PassReset.ps1` (new function before line 748), `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Install-PassReset: Resolve-DependencyAction' {
    It "returns 'install' when -InstallDependencies yes" {
        Resolve-DependencyAction -InstallDependencies 'yes' -Force $false | Should -Be 'install'
    }
    It "returns 'abort' when -InstallDependencies no" {
        Resolve-DependencyAction -InstallDependencies 'no' -Force $false | Should -Be 'abort'
    }
    It "returns 'install' under -Force even when InstallDependencies is prompt (CI safety)" {
        Resolve-DependencyAction -InstallDependencies 'prompt' -Force $true | Should -Be 'install'
    }
    It "returns 'prompt' for interactive default" {
        Resolve-DependencyAction -InstallDependencies 'prompt' -Force $false | Should -Be 'prompt'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Resolve-DependencyAction*'"
```

- [ ] **Step 3 — Minimal implementation.** Add before line 748:

```powershell
function Resolve-DependencyAction {
    <#
        STAB-006: decide how to handle a missing prerequisite without prompting in
        non-interactive contexts. -Force implies auto-install (safe CI behavior).
    #>
    param(
        [ValidateSet('prompt','yes','no')]
        [string] $InstallDependencies = 'prompt',
        [bool]   $Force
    )
    if ($Force)                          { return 'install' }
    switch ($InstallDependencies) {
        'yes'    { return 'install' }
        'no'     { return 'abort' }
        default  { return 'prompt' }
    }
}
```

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Resolve-DependencyAction*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add Resolve-DependencyAction for non-interactive prereqs [STAB-006 #21]"
```

### Task 21.2 — Failing test: `-InstallDependencies` + `-SkipDependencyCheck` params

**Files:** `deploy/Install-PassReset.ps1:85-121`

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Install-PassReset: dependency control parameters' {
    BeforeAll {
        $t=$null;$e=$null
        $ast=[System.Management.Automation.Language.Parser]::ParseFile(
            "$PSScriptRoot/Install-PassReset.ps1",[ref]$t,[ref]$e)
        $script:Names=$ast.ParamBlock.Parameters.Name.VariablePath.UserPath
    }
    It 'declares -InstallDependencies' { $script:Names | Should -Contain 'InstallDependencies' }
    It 'declares -SkipDependencyCheck' { $script:Names | Should -Contain 'SkipDependencyCheck' }
    It 'help text documents both flags' {
        $raw = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw
        $raw | Should -Match '\.PARAMETER InstallDependencies'
        $raw | Should -Match '\.PARAMETER SkipDependencyCheck'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*dependency control parameters*'"
```

- [ ] **Step 3 — Minimal implementation.** (a) Help text after the `-Force` param doc (~line 71):

```powershell
.PARAMETER InstallDependencies
    Controls prerequisite auto-install: 'prompt' (default, interactive Y/N),
    'yes' (auto-install missing IIS features and .NET Hosting Bundle), or 'no'
    (abort cleanly when a prerequisite is missing). -Force implies 'yes'.

.PARAMETER SkipDependencyCheck
    Skip all prerequisite detection (IIS features + .NET Hosting Bundle). Use only
    on hosts you have already validated. The installer proceeds straight to site setup.
```

(b) Params (after `[switch] $Reconfigure,` from Task 20.3):

```powershell
    [ValidateSet('prompt','yes','no')]
    [string] $InstallDependencies = 'prompt',

    [switch] $SkipDependencyCheck,
```

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*dependency control parameters*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add -InstallDependencies and -SkipDependencyCheck flags [STAB-006 #21]"
```

### Task 21.3 — Failing test: reboot-pending detection from DISM exit code

**Files:** `deploy/Install-PassReset.ps1` (new function before line 748)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Install-PassReset: Test-DismRebootPending' {
    It 'returns $true on DISM exit 3010' {
        Test-DismRebootPending -ExitCodes @(0, 3010, 0) | Should -BeTrue
    }
    It 'returns $false when all exits are 0' {
        Test-DismRebootPending -ExitCodes @(0, 0) | Should -BeFalse
    }
    It 'returns $false for an empty set' {
        Test-DismRebootPending -ExitCodes @() | Should -BeFalse
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Test-DismRebootPending*'"
```

- [ ] **Step 3 — Minimal implementation.** Add before line 748:

```powershell
function Test-DismRebootPending {
    <# STAB-006: DISM exit 3010 = success but a reboot is required to complete. #>
    param([int[]] $ExitCodes)
    return [bool]($ExitCodes | Where-Object { $_ -eq 3010 })
}
```

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Test-DismRebootPending*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add Test-DismRebootPending helper [STAB-006 #21]"
```

### Task 21.4 — Wire IIS-feature block: collect DISM exits, re-check, abort on reboot-pending

**Files:** `deploy/Install-PassReset.ps1:840-868`

- [ ] **Step 1 — Write failing test.** Append a source/AST test asserting the IIS block now (a) routes through `Resolve-DependencyAction`, (b) collects DISM exit codes, (c) re-reads `Get-WindowsFeature` after DISM, (d) aborts via `Test-DismRebootPending`:

```powershell
Describe 'Install-PassReset: IIS prereq block wiring' {
    BeforeAll { $script:Src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw }
    It 'uses Resolve-DependencyAction for the missing-feature decision' {
        $script:Src | Should -Match 'Resolve-DependencyAction'
    }
    It 'collects DISM exit codes for reboot detection' {
        $script:Src | Should -Match 'Test-DismRebootPending'
    }
    It 're-validates IIS features after DISM' {
        # A second Get-WindowsFeature pass after the DISM loop.
        ([regex]::Matches($script:Src, 'Get-WindowsFeature')).Count | Should -BeGreaterThan 1
    }
    It 'honors -SkipDependencyCheck' {
        $script:Src | Should -Match '\$SkipDependencyCheck'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*IIS prereq block wiring*'"
```

- [ ] **Step 3 — Minimal implementation.** Wrap the IIS prerequisite phase (line 811) with the skip guard, and replace the missing-feature handling (lines 837-868) so it routes through the resolver, collects exits, re-checks, and aborts on reboot-pending. Edit the block to:

```powershell
if ($HostingMode -eq 'IIS' -and -not $SkipDependencyCheck) {
```

…and replace lines 837-868 with:

```powershell
    if ($missing) {
        Write-Warn 'Missing IIS features detected:'
        $missing | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }

        $action = Resolve-DependencyAction -InstallDependencies $InstallDependencies -Force $Force.IsPresent
        if ($action -eq 'prompt') {
            $consent = Read-Host '  Install missing IIS features now via DISM? [Y/N]'
            $action  = if ($consent -match '^[Yy]') { 'install' } else { 'abort' }
        }
        if ($action -eq 'abort') {
            Write-Host ''
            Write-Host '  Missing IIS roles/features (not installed):' -ForegroundColor Yellow
            Write-Host '  To install manually, run as Administrator:' -ForegroundColor Yellow
            foreach ($f in $missing) {
                Write-Host "    dism /online /enable-feature /featurename:$f /all /norestart" -ForegroundColor Yellow
            }
            Write-Host ''
            exit 0
        }
        Write-Ok 'Installing missing IIS features via DISM'

        $dismExits = @()
        foreach ($f in $missing) {
            if ($PSCmdlet.ShouldProcess("IIS feature $f", 'Enable via DISM')) {
                $code = (Start-Process -FilePath dism.exe `
                    -ArgumentList @('/online','/enable-feature',"/featurename:$f",'/all','/norestart','/quiet') `
                    -Wait -PassThru -NoNewWindow).ExitCode
                $dismExits += $code
                if ($code -ne 0 -and $code -ne 3010) {
                    Abort "DISM failed enabling $f (exit $code). Run: dism /online /get-featureinfo /featurename:$f"
                }
            }
        }
        Write-Ok 'IIS features enabled via DISM'

        # STAB-006: a reboot is required to complete feature install — do NOT proceed
        # to site/pool creation, the worker process would start without the roles present.
        if (Test-DismRebootPending -ExitCodes $dismExits) {
            Abort 'IIS feature installation requires a system reboot (DISM exit 3010). Reboot and re-run the installer.'
        }

        # STAB-006: re-validate after install; abort with an explicit list if anything is still missing.
        $stillMissing = $requiredFeatures | Where-Object {
            (Get-WindowsFeature -Name $_).InstallState -ne 'Installed'
        }
        if ($stillMissing) {
            Write-Warn 'These IIS features are still not installed after DISM:'
            $stillMissing | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
            Abort 'Prerequisite IIS features could not be installed. Resolve manually and re-run.'
        }
        Write-Ok 'All required IIS features present (re-validated post-install)'
    } else {
        Write-Ok 'All required IIS features present'
    }
}
```

> **What could break:** When the new flags are absent, `Resolve-DependencyAction` returns `'prompt'` (no `-Force`) → identical interactive Y/N as before; `-Force` returns `'install'` → identical to the old `-Force` DISM path. The new abort-on-3010 changes behavior **only** in the genuine reboot-pending case (previously the script silently continued — that was the bug). `Get-WindowsFeature` is only reachable in IIS mode (unchanged guard), so non-IIS modes still skip it.

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*IIS prereq block wiring*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): re-check IIS features post-DISM and stop on reboot-pending [STAB-006 #21]"
```

### Task 21.5 — .NET Hosting Bundle: structured missing-list + auto-install + re-check

**Files:** `deploy/Install-PassReset.ps1:871-895`

- [ ] **Step 1 — Write failing test.** First add a pure helper to format the diagnostic, then test it. Append:

```powershell
Describe 'Install-PassReset: Get-HostingBundleDiagnostic' {
    It 'reports not-detected when version is null' {
        Get-HostingBundleDiagnostic -InstalledVersion $null |
            Should -Match 'not detected in HKLM registry'
    }
    It 'reports incompatible version when not 10.x' {
        $msg = Get-HostingBundleDiagnostic -InstalledVersion '8.0.11'
        $msg | Should -Match '8\.0\.11'
        $msg | Should -Match '10\.0\.0 or later'
    }
    It 'returns empty when version is 10.x' {
        Get-HostingBundleDiagnostic -InstalledVersion '10.0.3' | Should -BeNullOrEmpty
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Get-HostingBundleDiagnostic*'"
```

- [ ] **Step 3 — Minimal implementation.** Add helper before line 748:

```powershell
function Get-HostingBundleDiagnostic {
    <# STAB-006: structured "what is missing" message for the .NET Hosting Bundle. #>
    param([string] $InstalledVersion)
    if (-not $InstalledVersion) {
        return 'Missing: ASP.NET Core 10.0 Hosting Bundle (not detected in HKLM registry: SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost).'
    }
    if ($InstalledVersion -notmatch '^10\.') {
        return "Found incompatible .NET Hosting Bundle: $InstalledVersion — required: 10.0.0 or later."
    }
    return $null
}
```

Then rewrite the .NET block (lines 876-894). When `-SkipDependencyCheck` is set, skip; otherwise emit the structured message and respect the dependency action (auto-install attempt via `winget`, then re-query the registry):

```powershell
$bundleDiag = Get-HostingBundleDiagnostic -InstalledVersion ($hostingBundle.Version)
if (-not $SkipDependencyCheck -and $bundleDiag) {
    Write-Warn $bundleDiag
    Write-Host '  Required: ASP.NET Core 10.0 Runtime (Hosting Bundle)' -ForegroundColor Yellow
    Write-Host '  Download: https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Yellow

    $action = Resolve-DependencyAction -InstallDependencies $InstallDependencies -Force $Force.IsPresent
    if ($action -eq 'prompt') {
        $consent = Read-Host '  Attempt automatic install via winget now? [Y/N]'
        $action  = if ($consent -match '^[Yy]') { 'install' } else { 'abort' }
    }
    if ($action -eq 'install' -and (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Ok 'Installing .NET 10 Hosting Bundle via winget'
        if ($PSCmdlet.ShouldProcess('.NET 10 Hosting Bundle', 'Install via winget')) {
            Start-Process -FilePath winget -ArgumentList @(
                'install','--id','Microsoft.DotNet.HostingBundle.10','-e',
                '--accept-source-agreements','--accept-package-agreements') -Wait -NoNewWindow
        }
        # STAB-006: re-query the registry; only proceed if a 10.x bundle is now present.
        $hostingBundle = Get-ItemProperty `
            -Path 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost' `
            -ErrorAction SilentlyContinue
        $bundleDiag = Get-HostingBundleDiagnostic -InstalledVersion ($hostingBundle.Version)
        if ($bundleDiag) {
            Abort "$bundleDiag Re-run the installer after a successful Hosting Bundle install (a reboot may be required)."
        }
    } else {
        Write-Host '  Re-run this installer after the Hosting Bundle is installed.' -ForegroundColor Yellow
        exit 0
    }
}
$installedRuntime = $hostingBundle.Version
Write-Ok ".NET Hosting Bundle $installedRuntime detected"
```

> **What could break:** When the bundle is already 10.x, `$bundleDiag` is `$null` → the whole new block is skipped and behavior is identical to before. When `winget` is absent, the `else` branch reproduces the old "re-run after install; exit 0" path. `$hostingBundle.Version` is read via `Get-HostingBundleDiagnostic`, which tolerates `$hostingBundle = $null` because the param binds `$null.Version` to `$null` under non-strict access — verify by running the helper test with `$null`. Note: `$hostingBundle.Version` at module scope under `Set-StrictMode` — guard by reading into a local first if strict-mode complains (the helper already takes the value, not the object).

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Get-HostingBundleDiagnostic*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): structured .NET bundle diagnostics + optional auto-install [STAB-006 #21]"
```

### Task 21.6 — Update prerequisite documentation

**Files:** `docs/IIS-Setup.md:60-75` (Step 1), `docs/IIS-Setup.md:346-389` (Troubleshooting)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Docs: STAB-006 prerequisite flags documented' {
    BeforeAll { $script:Iis = Get-Content "$PSScriptRoot/../docs/IIS-Setup.md" -Raw }
    It 'documents -InstallDependencies' { $script:Iis | Should -Match '-InstallDependencies' }
    It 'documents -SkipDependencyCheck' { $script:Iis | Should -Match '-SkipDependencyCheck' }
    It 'documents reboot-pending behavior' { $script:Iis | Should -Match '(?i)reboot' }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*prerequisite flags documented*'"
```

- [ ] **Step 3 — Minimal implementation.** In Step 1 (after line 73-75), add a flags subsection:

```markdown
### Unattended dependency handling (flags)

| Flag | Effect |
|------|--------|
| `-InstallDependencies prompt` | (default) interactive Y/N for missing IIS features and the .NET Hosting Bundle |
| `-InstallDependencies yes` | auto-install missing IIS features (DISM) and the Hosting Bundle (winget) without prompting |
| `-InstallDependencies no` | abort cleanly (exit 0) when any prerequisite is missing |
| `-SkipDependencyCheck` | skip all prerequisite detection (use only on pre-validated hosts) |

`-Force` implies `-InstallDependencies yes`. After installing IIS features the installer
**re-validates** them; if DISM reports a pending reboot (exit 3010) the installer aborts
**before** creating the site/app pool — reboot and re-run.
```

In Troubleshooting (after line 389), add:

```markdown
### Reboot pending after DISM

If the installer aborts with "IIS feature installation requires a system reboot
(DISM exit 3010)", reboot the server and re-run the installer. This is intentional:
creating the site before the roles finish installing would start the worker without IIS roles present.

### Installer aborted before site creation

The installer performs all prerequisite checks (IIS features + .NET 10 Hosting Bundle)
**before** any IIS site/app-pool change. If a prerequisite cannot be satisfied it aborts
without partial state. Re-run after resolving the listed item, or use `-SkipDependencyCheck`
on a host you have already validated.
```

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*prerequisite flags documented*'"
```

- [ ] **Step 5 — Commit.**
```
git add docs/IIS-Setup.md deploy/Install-PassReset.Tests.ps1 && git commit -m "docs(installer): document dependency flags and reboot-pending behavior [STAB-006 #21]"
```

### Task 21.7 — Verify acceptance criteria & close #21

- [ ] Full suite green: `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"`.
- [ ] Confirm each #21 criterion: interactive auto-install option (IIS **and** .NET); re-check after install with continue-only-when-met; non-interactive predictable behavior via `-InstallDependencies`/`-SkipDependencyCheck`/`-Force`; clear missing-IIS list (already present); structured missing-.NET list (new); prereq checks run before site/pool changes (re-validated, reboot-pending stops before line 1335 site creation); docs updated.
- [ ] Close:
```
gh issue close 21 --reason completed --comment "STAB-006 fixed: added -InstallDependencies/-SkipDependencyCheck, post-DISM IIS re-check, reboot-pending abort before site creation, .NET Hosting Bundle structured diagnostics + optional winget auto-install + re-check. Helpers unit-tested (Resolve-DependencyAction, Test-DismRebootPending, Get-HostingBundleDiagnostic). Docs updated in IIS-Setup.md."
```

---

## Issue #39 (STAB-005) — Release Quality / CI Gates

**Root cause (verified at HEAD):** Neither `ci.yml` nor `release.yml` validates PowerShell syntax. `.editorconfig` already has a `[*.{ps1,psm1,psd1}]` section with `charset=utf-8` + `insert_final_newline=true` + `trim_trailing_whitespace=true` (so the *encoding standard* criterion is already MET in policy) — but **no CI enforcement** exists, and `Uninstall-PassReset.ps1` has zero test coverage.

**Risk: low.** Placed before #34 so the gate exists when the high-risk #34 changes land.

### Task 39.1 — Failing test: Uninstall script parses, declares params, guards -KeepFiles

**Files:** `deploy/Uninstall-PassReset.Tests.ps1` (**create**)

- [ ] **Step 1 — Write failing test.** Create `deploy/Uninstall-PassReset.Tests.ps1`. Because the uninstaller has no test-mode short-circuit, validate it by **AST inspection** (never executes the destructive flow):

```powershell
# Pester tests for Uninstall-PassReset.ps1.
# The uninstaller has no test-mode guard and runs destructive IIS/file removal at
# top level, so we validate it via AST inspection only — never dot-source/execute it.
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Uninstall-PassReset.Tests.ps1 -Output Detailed"

BeforeAll {
    $script:Path = "$PSScriptRoot/Uninstall-PassReset.ps1"
    $t = $null; $e = $null
    $script:Ast    = [System.Management.Automation.Language.Parser]::ParseFile($script:Path, [ref]$t, [ref]$e)
    $script:Errors = $e
    $script:Src    = Get-Content $script:Path -Raw
    $script:ParamNames = $script:Ast.ParamBlock.Parameters.Name.VariablePath.UserPath
}

Describe 'Uninstall-PassReset: parses without errors' {
    It 'has zero parser errors' {
        $script:Errors.Count | Should -Be 0
    }
}

Describe 'Uninstall-PassReset: parameters' {
    It 'declares -KeepFiles'     { $script:ParamNames | Should -Contain 'KeepFiles' }
    It 'declares -RemoveBackups' { $script:ParamNames | Should -Contain 'RemoveBackups' }
    It 'declares -Force'         { $script:ParamNames | Should -Contain 'Force' }
}

Describe 'Uninstall-PassReset: -KeepFiles guards file removal' {
    It 'wraps Remove-Item on the physical path in a (-not $KeepFiles) branch' {
        $script:Src | Should -Match 'if \(-not \$KeepFiles\)'
    }
    It 'warns that files are retained when -KeepFiles is set' {
        $script:Src | Should -Match 'Files retained'
    }
}

Describe 'Uninstall-PassReset: IIS + service removal present' {
    It 'removes the IIS site'      { $script:Src | Should -Match 'Remove-Website -Name \$SiteName' }
    It 'removes the app pool'      { $script:Src | Should -Match 'Remove-WebAppPool -Name \$AppPoolName' }
    It 'removes the Windows service' { $script:Src | Should -Match 'sc\.exe delete \$SiteName' }
}

Describe 'Uninstall-PassReset: -Force skips confirmation' {
    It 'gates Read-Host on (-not $Force)' {
        $script:Src | Should -Match 'if \(-not \$Force\)'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL** (file does not exist yet — Pester reports no tests / path error; create then re-run shows the assertions, which should pass against the *already-correct* uninstaller. This task primarily establishes coverage, so after creating the file the tests pass immediately — confirming the gate works):
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Uninstall-PassReset.Tests.ps1 -Output Detailed"
```

- [ ] **Step 3 — Implementation.** No production code change — the uninstaller is already correct. This task *is* the test file. (If any assertion fails, that is a real latent bug — root-cause via `superpowers:systematic-debugging` before adjusting.)

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Uninstall-PassReset.Tests.ps1 -Output Detailed"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Uninstall-PassReset.Tests.ps1 && git commit -m "test(installer): add Pester AST coverage for Uninstall-PassReset [STAB-005 #39]"
```

### Task 39.2 — Failing test: all deploy/*.ps1 tokenize cleanly (local gate)

**Files:** `deploy/Install-PassReset.Tests.ps1` (append) — a portable parse-check that the CI step will mirror

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'PowerShell scripts: parse cleanly' {
    It 'every deploy/*.ps1 has zero parser errors' {
        $deploy = Split-Path $PSScriptRoot -Parent
        $bad = @()
        Get-ChildItem (Join-Path $deploy 'deploy') -Filter '*.ps1' | ForEach-Object {
            $t = $null; $e = $null
            [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$t, [ref]$e) | Out-Null
            if ($e.Count -gt 0) { $bad += "$($_.Name): $($e.Count) error(s)" }
        }
        $bad -join '; ' | Should -BeNullOrEmpty
    }
}
```

- [ ] **Step 2 — Run, expect PASS** (all scripts currently parse; this pins it):
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*parse cleanly*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.Tests.ps1 && git commit -m "test(installer): pin parse-check for all deploy scripts [STAB-005 #39]"
```

### Task 39.3 — Add `powershell-quality` job to ci.yml (parse + analyzer + encoding + Pester)

**Files:** `.github/workflows/ci.yml` (add a new job after the `build` job, before/after `tests`)

- [ ] **Step 1 — Write failing test.** Add a workflow-content assertion to the Pester suite:

```powershell
Describe 'CI: PowerShell quality gate present' {
    BeforeAll {
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        # PSScriptRoot is .../deploy ; repo root is its parent.
        $repo = Split-Path $PSScriptRoot -Parent
        $script:Ci = Get-Content (Join-Path $repo '.github/workflows/ci.yml') -Raw
        $script:Rel = Get-Content (Join-Path $repo '.github/workflows/release.yml') -Raw
    }
    It 'ci.yml defines a powershell-quality job' {
        $script:Ci | Should -Match 'powershell-quality:'
    }
    It 'ci.yml runs PSScriptAnalyzer' {
        $script:Ci | Should -Match 'PSScriptAnalyzer'
    }
    It 'ci.yml runs the installer Pester suites' {
        $script:Ci | Should -Match 'Invoke-Pester'
    }
    It 'ci.yml enforces no UTF-8 BOM on deploy scripts' {
        $script:Ci | Should -Match '(?i)BOM'
    }
    It 'release.yml gates release on powershell-quality' {
        $script:Rel | Should -Match 'needs:'
        $script:Rel | Should -Match 'powershell-quality'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*PowerShell quality gate*'"
```

- [ ] **Step 3 — Minimal implementation.** In `.github/workflows/ci.yml`, add a new job (sibling to `build` and `tests`, after the `build` job block ends at line 65 and before `tests:` at line 67):

```yaml
  powershell-quality:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v6.0.2

      - name: Parse-check all deploy/*.ps1
        shell: pwsh
        run: |
          $bad = @()
          Get-ChildItem deploy -Filter *.ps1 | ForEach-Object {
            $t = $null; $e = $null
            [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$t, [ref]$e) | Out-Null
            if ($e.Count -gt 0) {
              foreach ($err in $e) { Write-Host "::error file=$($_.FullName)::$($err.Message)" }
              $bad += $_.Name
            }
          }
          if ($bad.Count -gt 0) { Write-Error "Parse errors in: $($bad -join ', ')"; exit 1 }
          Write-Host "All deploy/*.ps1 parsed cleanly."

      - name: Encoding check (no UTF-8 BOM in deploy/*.ps1)
        shell: pwsh
        run: |
          $bom = @()
          Get-ChildItem deploy -Filter *.ps1 | ForEach-Object {
            $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
            if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
              Write-Host "::error file=$($_.FullName)::File has a UTF-8 BOM (must be UTF-8 without BOM per .editorconfig)"
              $bom += $_.Name
            }
          }
          if ($bom.Count -gt 0) { Write-Error "UTF-8 BOM found in: $($bom -join ', ')"; exit 1 }
          Write-Host "All deploy/*.ps1 are UTF-8 without BOM."

      - name: PSScriptAnalyzer
        shell: pwsh
        run: |
          Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
          Install-Module PSScriptAnalyzer -Scope CurrentUser -Force -ErrorAction Stop
          $results = Invoke-ScriptAnalyzer -Path deploy -Recurse `
            -Severity @('Error','Warning') `
            -ExcludeRule @('PSAvoidUsingWriteHost','PSUseShouldProcessForStateChangingFunctions','PSReviewUnusedParameter')
          if ($results) {
            $results | Format-Table -AutoSize | Out-String | Write-Host
            $errors = @($results | Where-Object Severity -eq 'Error')
            if ($errors.Count -gt 0) { Write-Error "PSScriptAnalyzer found $($errors.Count) error(s)."; exit 1 }
            Write-Warning "PSScriptAnalyzer warnings present (not gating)."
          } else {
            Write-Host "PSScriptAnalyzer: clean."
          }

      - name: Pester (installer + uninstaller suites)
        shell: pwsh
        run: |
          Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
          Install-Module Pester -MinimumVersion 5.5.0 -Scope CurrentUser -Force -SkipPublisherCheck
          $cfg = New-PesterConfiguration
          $cfg.Run.Path = @('deploy/Install-PassReset.Tests.ps1','deploy/Uninstall-PassReset.Tests.ps1')
          $cfg.Output.Verbosity = 'Detailed'
          $cfg.Run.Exit = $true
          Invoke-Pester -Configuration $cfg
```

> **What could break:** `PSAvoidUsingWriteHost` is excluded because the installer intentionally uses `Write-Host` for colored operator output throughout — gating on it would fail the whole existing script. Start the analyzer gate at **Error** severity only (warnings printed, not gating) to avoid a wall of pre-existing style warnings blocking CI; this can be tightened later. The `ConvertFrom-Json`/strict-mode code already runs in tests, so Pester install adds ~30s.

- [ ] **Step 4 — Run, expect PASS** (the Pester content-assertion test):
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*PowerShell quality gate*'"
```
Then validate the workflow YAML parses:
```
pwsh -NoProfile -Command "Get-Content .github/workflows/ci.yml -Raw | Out-Null; Write-Host 'ci.yml read OK'"
```

- [ ] **Step 5 — Commit.**
```
git add .github/workflows/ci.yml deploy/Install-PassReset.Tests.ps1 && git commit -m "ci: add powershell-quality gate (parse, encoding, PSScriptAnalyzer, Pester) [STAB-005 #39]"
```

### Task 39.4 — Gate the release on the PowerShell quality job

**Files:** `.github/workflows/release.yml:18-20`

- [ ] **Step 1 — Test already written** in Task 39.3 (the `release.yml gates release on powershell-quality` assertion). Run it, expect FAIL until this task:
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*release.yml gates*'"
```

- [ ] **Step 3 — Minimal implementation.** In `.github/workflows/release.yml`, add a `powershell-quality` job and make `release` depend on it. Add after the `tests:` job (line 16) :

```yaml
  powershell-quality:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v6.0.2
      - name: Parse-check all deploy/*.ps1
        shell: pwsh
        run: |
          $bad = @()
          Get-ChildItem deploy -Filter *.ps1 | ForEach-Object {
            $t = $null; $e = $null
            [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$t, [ref]$e) | Out-Null
            if ($e.Count -gt 0) { $bad += $_.Name }
          }
          if ($bad.Count -gt 0) { Write-Error "Parse errors in: $($bad -join ', ')"; exit 1 }
      - name: Pester (installer + uninstaller suites)
        shell: pwsh
        run: |
          Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
          Install-Module Pester -MinimumVersion 5.5.0 -Scope CurrentUser -Force -SkipPublisherCheck
          $cfg = New-PesterConfiguration
          $cfg.Run.Path = @('deploy/Install-PassReset.Tests.ps1','deploy/Uninstall-PassReset.Tests.ps1')
          $cfg.Run.Exit = $true
          Invoke-Pester -Configuration $cfg
```

Then change the `release` job's `needs:` (line 19) from:

```yaml
  release:
    needs: tests
```

to:

```yaml
  release:
    needs: [tests, powershell-quality]
```

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*release.yml gates*'"
```

- [ ] **Step 5 — Commit.**
```
git add .github/workflows/release.yml && git commit -m "ci: block release on powershell-quality gate [STAB-005 #39]"
```

### Task 39.5 — Confirm `.editorconfig` ps1 encoding policy (no change expected)

**Files:** `.editorconfig` (verify only)

- [ ] **Step 1 — Write failing test.** Append a pin so the policy can't silently regress:

```powershell
Describe 'editorconfig: ps1 encoding policy' {
    It 'has a [*.ps1...] section enforcing utf-8' {
        $repo = Split-Path $PSScriptRoot -Parent
        $ec = Get-Content (Join-Path $repo '.editorconfig') -Raw
        $ec | Should -Match '\[\*\.\{ps1[^\]]*\}\]'
        $ec | Should -Match 'charset = utf-8'
    }
}
```

- [ ] **Step 2 — Run, expect PASS** (section already present at `.editorconfig:17` + global `charset = utf-8` at line 4):
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*ps1 encoding policy*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.Tests.ps1 && git commit -m "test: pin .editorconfig ps1 utf-8 policy [STAB-005 #39]"
```

### Task 39.6 — Verify acceptance criteria & close #39

- [ ] Local suites green: `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1, deploy/Uninstall-PassReset.Tests.ps1 -Output Detailed"`.
- [ ] Push branch and confirm the new `powershell-quality` job runs green in Actions (parse + encoding + analyzer + Pester), and that `release` lists it under `needs`.
- [ ] Confirm #39 criteria: release prevents shipping invalid scripts (gate in both ci.yml and release.yml); Uninstall parses without ParserError (parse-check + AST test); site/pool removal covered (Uninstall tests); `-KeepFiles` covered; encoding consistent (`.editorconfig` + BOM check).
- [ ] Close:
```
gh issue close 39 --reason completed --comment "STAB-005 fixed: added a powershell-quality CI gate (PowerShell parse-check, UTF-8/no-BOM encoding check, PSScriptAnalyzer at Error severity, and Pester) to ci.yml and gated release.yml on it. Added deploy/Uninstall-PassReset.Tests.ps1 covering parse, params, -KeepFiles guard, IIS/pool/service removal, and -Force. .editorconfig ps1 policy pinned."
```

---

## Issue #34 (STAB-019) — Post-Deploy Health Verification  ⚠️ HIGH REGRESSION RISK — LAST

> **PREREQUISITE — do not start until merged:** the **schema/config plan** and the **health plan (#31)** are merged and `GET /api/health` returns `{ status, checks: { ad, smtp, expiryService } }`; **#32 (TLS)** binding work is merged so the HTTPS verifier target exists; and **#24/#26/#27** (the aggregate-status contract this issue consumes) are complete. Verify with `Grep`/by hitting a dev `/api/health` before writing 34.x code.

**Root cause (verified at HEAD):**
1. The health block (`deploy/Install-PassReset.ps1:1481-1506`) accepts any HTTP 200 — it never checks `$body.status`. A 200 with `status: degraded` passes.
2. `$hostHeader = $env:COMPUTERNAME` (line 1434) is hardcoded — custom host-header bindings fail.
3. The failure message (line 1493) prints only the body — no `$logsPath` / Event Viewer / binding pointers.
4. The whole block is inside `if ($HostingMode -eq 'IIS')` — Service/Console modes get no verification. (Note: in the current file the block actually sits before the `# end if IIS` at line 1798, so confirm scope during execution and move as needed.)

**Risk: HIGH.** Regression guards on every task: the happy path (healthy 200) must still pass; `-SkipHealthCheck` must still bypass; the existing retry loop and JSON-parse fallback must be preserved.

### Task 34.1 — Failing test: health-host-header resolver

**Files:** `deploy/Install-PassReset.ps1` (function before line 748), `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Install-PassReset: Resolve-HealthHostHeader' {
    It 'extracts a custom hostname from BindingInformation *:443:passreset.corp.local' {
        Resolve-HealthHostHeader -BindingInformation '*:443:passreset.corp.local' -Fallback 'HOST01' |
            Should -Be 'passreset.corp.local'
    }
    It 'falls back to COMPUTERNAME for a wildcard binding *:80:' {
        Resolve-HealthHostHeader -BindingInformation '*:80:' -Fallback 'HOST01' |
            Should -Be 'HOST01'
    }
    It 'falls back when BindingInformation is null/empty' {
        Resolve-HealthHostHeader -BindingInformation $null -Fallback 'HOST01' |
            Should -Be 'HOST01'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Resolve-HealthHostHeader*'"
```

- [ ] **Step 3 — Minimal implementation.** Add before line 748:

```powershell
function Resolve-HealthHostHeader {
    <#
        STAB-019: derive the host the post-deploy health check should target from an
        IIS binding's BindingInformation ("*:port:hostname"). A wildcard/empty host
        means "all hostnames" — fall back to the machine name so the loopback request
        actually resolves.
    #>
    param(
        [string] $BindingInformation,
        [string] $Fallback
    )
    if ([string]::IsNullOrWhiteSpace($BindingInformation)) { return $Fallback }
    $parts = $BindingInformation -split ':'
    $host  = if ($parts.Count -ge 3) { $parts[2] } else { '' }
    if ([string]::IsNullOrWhiteSpace($host)) { return $Fallback }
    return $host
}
```

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Resolve-HealthHostHeader*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add Resolve-HealthHostHeader for custom host bindings [STAB-019 #34]"
```

### Task 34.2 — Failing test: aggregate-status evaluator

**Files:** `deploy/Install-PassReset.ps1` (function before line 748), `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Install-PassReset: Test-HealthResponseHealthy' {
    It 'returns $true when status is healthy' {
        Test-HealthResponseHealthy -HealthJson '{"status":"healthy","checks":{}}' | Should -BeTrue
    }
    It 'returns $false when status is degraded' {
        Test-HealthResponseHealthy -HealthJson '{"status":"degraded","checks":{}}' | Should -BeFalse
    }
    It 'returns $false when status is unhealthy' {
        Test-HealthResponseHealthy -HealthJson '{"status":"unhealthy","checks":{}}' | Should -BeFalse
    }
    It 'returns $false on unparseable body (fail closed)' {
        Test-HealthResponseHealthy -HealthJson 'not json' | Should -BeFalse
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Test-HealthResponseHealthy*'"
```

- [ ] **Step 3 — Minimal implementation.** Add before line 748:

```powershell
function Test-HealthResponseHealthy {
    <#
        STAB-019: a 200 is necessary but not sufficient — the /api/health aggregate
        'status' must be 'healthy'. Unparseable bodies fail closed.
    #>
    param([string] $HealthJson)
    try {
        $obj = $HealthJson | ConvertFrom-Json -ErrorAction Stop
        return ($obj.status -eq 'healthy')
    } catch {
        return $false
    }
}
```

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Test-HealthResponseHealthy*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add Test-HealthResponseHealthy aggregate-status check [STAB-019 #34]"
```

### Task 34.3 — Failing test: failure-diagnostics builder

**Files:** `deploy/Install-PassReset.ps1` (function before line 748), `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Install-PassReset: Get-HealthFailureDiagnostics' {
    BeforeAll {
        $script:Diag = Get-HealthFailureDiagnostics -BaseUrl 'https://host01:443' -LogsPath 'C:\inetpub\logs\PassReset'
    }
    It 'references the logs path'        { $script:Diag | Should -Match 'C:\\inetpub\\logs\\PassReset' }
    It 'references Event Viewer'         { $script:Diag | Should -Match '(?i)Event Viewer' }
    It 'references the base URL'         { $script:Diag | Should -Match 'https://host01:443' }
    It 'mentions binding/port checks'    { $script:Diag | Should -Match '(?i)binding' }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Get-HealthFailureDiagnostics*'"
```

- [ ] **Step 3 — Minimal implementation.** Add before line 748:

```powershell
function Get-HealthFailureDiagnostics {
    <# STAB-019: actionable multi-line pointer block printed on health-check failure. #>
    param(
        [string] $BaseUrl,
        [string] $LogsPath
    )
    return @"
Post-deploy health check failed for $BaseUrl.

Troubleshooting:
  1. Logs: inspect $LogsPath for ASP.NET Core startup and request errors.
  2. Event Viewer -> Windows Logs -> Application, Source 'PassReset' (ID 1001) for config/startup failures.
  3. App pool: open IIS Manager and confirm the PassReset app pool is Started (not stopped on a crash).
  4. Binding: confirm the site's host header and port match $BaseUrl (mismatch -> connection refused / 404).
  5. Common causes: wrong app-pool identity, occupied port, HTTPS cert not bound, appsettings.Production.json schema errors.
"@
}
```

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Get-HealthFailureDiagnostics*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add Get-HealthFailureDiagnostics block [STAB-019 #34]"
```

### Task 34.4 — Wire host-header extraction into the announce + health block

**Files:** `deploy/Install-PassReset.ps1:1433-1455` (host header), `:1462-1467` (baseUrl)

- [ ] **Step 1 — Write failing test.** Append a source/AST test:

```powershell
Describe 'Install-PassReset: health host-header wiring' {
    BeforeAll { $script:Src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw }
    It 'derives $hostHeader from a binding via Resolve-HealthHostHeader' {
        $script:Src | Should -Match 'Resolve-HealthHostHeader'
    }
    It 'no longer hardcodes $hostHeader to COMPUTERNAME as the only source' {
        # COMPUTERNAME may still be the fallback arg, but it must be passed to the resolver.
        $script:Src | Should -Match 'Resolve-HealthHostHeader .*-Fallback .*COMPUTERNAME'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*health host-header wiring*'"
```

- [ ] **Step 3 — Minimal implementation.** Replace line 1434 (`$hostHeader = $env:COMPUTERNAME`) with logic that prefers the HTTPS binding host (the health check targets HTTPS when a cert is bound), else the HTTP binding host, else COMPUTERNAME:

```powershell
# STAB-019 D-03 / host-header: derive the announce + health host from the actual
# IIS binding so custom host headers (e.g. passreset.corp.local) are honored.
$hostHeader = $env:COMPUTERNAME
$httpsBindingInfo = (@(Get-IISSiteBinding -Name $SiteName -Protocol https -ErrorAction SilentlyContinue) |
    Select-Object -First 1).BindingInformation
$httpBindingInfo  = (@(Get-IISSiteBinding -Name $SiteName -Protocol http  -ErrorAction SilentlyContinue) |
    Select-Object -First 1).BindingInformation
if ($CertThumbprint -and $httpsBindingInfo) {
    $hostHeader = Resolve-HealthHostHeader -BindingInformation $httpsBindingInfo -Fallback $env:COMPUTERNAME
} elseif ($httpBindingInfo) {
    $hostHeader = Resolve-HealthHostHeader -BindingInformation $httpBindingInfo -Fallback $env:COMPUTERNAME
}
```

> **What could break:** This is inside the existing IIS-only region, where `Get-IISSiteBinding` is valid. The announce lines below (1435-1454) already use `$hostHeader`; they now get the real host. `$baseUrl` (line 1463-1467) already derives from `$hostHeader`, so it inherits the fix automatically. Regression: when binding is wildcard, `Resolve-HealthHostHeader` returns the fallback = COMPUTERNAME (old behavior preserved).

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*health host-header wiring*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "fix(installer): resolve health host header from IIS binding [STAB-019 #34]"
```

### Task 34.5 — Wire aggregate-status validation + diagnostics into the health block

**Files:** `deploy/Install-PassReset.ps1:1481-1506`

- [ ] **Step 1 — Write failing test.** Append a source test asserting the loop now requires healthy status and the failure path emits diagnostics:

```powershell
Describe 'Install-PassReset: health block validation wiring' {
    BeforeAll { $script:Src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw }
    It 'gates success on Test-HealthResponseHealthy, not just StatusCode 200' {
        $script:Src | Should -Match 'Test-HealthResponseHealthy'
    }
    It 'emits Get-HealthFailureDiagnostics on failure' {
        $script:Src | Should -Match 'Get-HealthFailureDiagnostics'
    }
    It 'still exits 1 on final failure' {
        $script:Src | Should -Match 'exit 1'
    }
    It 'still bypasses under -SkipHealthCheck' {
        $script:Src | Should -Match 'if \(-not \$SkipHealthCheck\)'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*health block validation wiring*'"
```

- [ ] **Step 3 — Minimal implementation.** In the retry loop (lines 1480-1489), require healthy status; replace the failure handler (1491-1495) to use the diagnostics builder; keep the JSON summary (1497-1506). The `$logsPath` is computed *after* this block, so compute the path inline. Replace lines 1483 and 1491-1495:

Loop success condition (replace line 1483):
```powershell
            if ($lastHealth.StatusCode -eq 200 -and $lastSettings.StatusCode -eq 200 -and
                (Test-HealthResponseHealthy -HealthJson $lastHealth.Content)) {
                $ok = $true
            }
```

Failure handler (replace lines 1491-1495):
```powershell
    if (-not $ok) {
        $healthLogsPath = Join-Path $env:SystemDrive 'inetpub\logs\PassReset'
        $bodySnippet = if ($lastHealth) { $lastHealth.Content } else { '(no response)' }
        Write-Host ''
        Write-Host (Get-HealthFailureDiagnostics -BaseUrl $baseUrl -LogsPath $healthLogsPath) -ForegroundColor Yellow
        Write-Error ("Post-deploy health check failed after {0} attempts. Last /api/health response: {1}" -f $maxAttempts, $bodySnippet)
        exit 1
    }
```

> **What could break:** This is the highest-risk change. The retry loop now keeps retrying when status is `degraded` (e.g. SMTP still warming up) — but the dependent **health plan must define which sub-checks are degraded-tolerant vs hard-fail**. If `degraded` is a legitimate steady state for some checks (e.g. expiry service disabled), `Test-HealthResponseHealthy` would wrongly fail. **Verification gate:** confirm with the merged health plan that aggregate `status: healthy` is the intended success contract (issue #34 explicitly says "any non-healthy aggregate status should trigger hard failure"). If the contract distinguishes "disabled" from "degraded", adjust `Test-HealthResponseHealthy` to also accept a documented allow-list — covered by the test in 34.2, extend it then. `-SkipHealthCheck` guard (line 1462) and the JSON summary fallback (1503-1505) are untouched.

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*health block validation wiring*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "fix(installer): fail post-deploy check on non-healthy status + add diagnostics [STAB-019 #34]"
```

### Task 34.6 — Extend verification to Service/Console modes

**Files:** `deploy/Install-PassReset.ps1:1799-1820` (mode branches), and the health block scope

- [ ] **Step 1 — Write failing test.** Append a source test asserting Service mode reports an endpoint and Console mode prints a diagnostic note:

```powershell
Describe 'Install-PassReset: non-IIS post-deploy reporting' {
    BeforeAll { $script:Src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw }
    It 'Service mode reports a health endpoint' {
        # The Service branch must mention /api/health for operator verification.
        $script:Src | Should -Match "Service mode.*(?s).*api/health"
    }
    It 'Console mode prints a verification note (app not auto-started)' {
        $script:Src | Should -Match 'Console mode.*(?s).*health'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*non-IIS post-deploy reporting*'"
```

- [ ] **Step 3 — Minimal implementation.** The full retry-based health check is IIS-scoped (it depends on IIS bindings). For Service mode the app self-hosts Kestrel on its configured HTTPS port; for Console mode the app is not auto-started. Add reporting to the existing branches. In the Service branch (after line 1815):

```powershell
    $svcBase = if ($CertThumbprint) { "https://${env:COMPUTERNAME}:${HttpsPort}" } else { "http://${env:COMPUTERNAME}:${selectedHttpPort}" }
    if (-not $SkipHealthCheck) {
        Write-Step "Verifying service at $svcBase/api/health (up to 10 x 2s)"
        $svcOk = $false
        for ($i = 1; $i -le 10 -and -not $svcOk; $i++) {
            Start-Sleep -Seconds 2
            try {
                $r = Invoke-WebRequest -Uri "$svcBase/api/health" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                if ($r.StatusCode -eq 200 -and (Test-HealthResponseHealthy -HealthJson $r.Content)) { $svcOk = $true }
            } catch { Write-Warning ("Attempt {0}/10: {1}" -f $i, $_.Exception.Message) }
        }
        if (-not $svcOk) {
            Write-Host (Get-HealthFailureDiagnostics -BaseUrl $svcBase -LogsPath (Join-Path $PhysicalPath 'logs')) -ForegroundColor Yellow
            Write-Error 'Service-mode post-deploy health check failed.'
            exit 1
        }
        Write-Ok "Service healthy at $svcBase/api/health"
    } else {
        Write-Step "Skipping service health check (-SkipHealthCheck)"
    }
```

In the Console branch (after line 1819):

```powershell
    Write-Host "Console mode: app is not auto-started, so no health check runs." -ForegroundColor Cyan
    Write-Host "After starting it manually, verify: Invoke-WebRequest https://localhost:${HttpsPort}/api/health" -ForegroundColor Cyan
```

> **What could break:** Service mode now hard-fails the installer if Kestrel doesn't answer — but the service was just started by `Install-AsWindowsService` (line 743 `Start-Service`). If service start is slow, the 10x2s loop (~20s) should cover cold start; if not, raise the count. `-SkipHealthCheck` bypasses for air-gapped hosts. Console mode never fails (app not started) — only prints guidance. **Verification:** confirm Service mode's default HTTPS port matches what the host actually binds (the health plan / #32 TLS work defines this); if the service binds 5001 not 443, adjust `$svcBase`. Validate against the merged Service-mode config before merging this task.

- [ ] **Step 4 — Run, expect PASS:**
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*non-IIS post-deploy reporting*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add post-deploy verification for Service and Console modes [STAB-019 #34]"
```

### Task 34.7 — Regression guard: healthy IIS path + -SkipHealthCheck still pass

**Files:** `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** A behavior test against the extracted evaluators that pins the happy path and the bypass:

```powershell
Describe 'Install-PassReset: STAB-019 regression guards' {
    It 'a healthy 200 body is accepted (happy path preserved)' {
        Test-HealthResponseHealthy -HealthJson '{"status":"healthy","checks":{"ad":{"status":"healthy"}}}' |
            Should -BeTrue
    }
    It 'host-header wildcard binding falls back to COMPUTERNAME (no behavior change for default installs)' {
        Resolve-HealthHostHeader -BindingInformation '*:443:' -Fallback 'DEFAULT' | Should -Be 'DEFAULT'
    }
    It 'health block still wrapped by -SkipHealthCheck guard' {
        (Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw) | Should -Match 'if \(-not \$SkipHealthCheck\)'
    }
}
```

- [ ] **Step 2 — Run, expect PASS** (guards already satisfied by 34.1/34.2/34.5):
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*STAB-019 regression guards*'"
```

- [ ] **Step 5 — Commit.**
```
git add deploy/Install-PassReset.Tests.ps1 && git commit -m "test(installer): regression guards for healthy path and -SkipHealthCheck [STAB-019 #34]"
```

### Task 34.8 — Verify acceptance criteria & close #34

- [ ] Full suites green: `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1, deploy/Uninstall-PassReset.Tests.ps1 -Output Detailed"`.
- [ ] Confirm the `powershell-quality` CI gate (from #39) passes on the branch with all #34 changes (PSScriptAnalyzer must not error on the new code).
- [ ] **Manual integration (required for HIGH risk, run on a test IIS host):**
  - Deploy with a custom host-header binding (not COMPUTERNAME) → `/health` check succeeds using the bound hostname.
  - Simulate degraded health (break SMTP config) → installer fails with the diagnostic block and exits 1.
  - Deploy in Service mode → health check runs against the service endpoint and reports healthy.
  - Run `-SkipHealthCheck` → verification is bypassed cleanly.
- [ ] Confirm all four #34 criteria: calls `/health` and fails if unhealthy; clear diagnostics/troubleshooting; supports HTTPS + host headers; runs in IIS/Service/Console.
- [ ] Close:
```
gh issue close 34 --reason completed --comment "STAB-019 fixed: post-deploy verification now (1) fails on any non-healthy aggregate /api/health status (Test-HealthResponseHealthy), (2) prints actionable diagnostics (logs path, Event Viewer, app-pool, binding) via Get-HealthFailureDiagnostics, (3) resolves the host header from the actual IIS binding (Resolve-HealthHostHeader) for custom hostnames + HTTPS, and (4) runs in IIS, Service, and Console modes. Helpers unit-tested; manual multi-homed UAT performed."
```

---

## Final integration & close-out

- [ ] Run the complete PowerShell suite locally once more:
```
pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1, deploy/Uninstall-PassReset.Tests.ps1 -Output Detailed"
```
- [ ] Push and confirm CI `powershell-quality` + `tests` + `build` are all green.
- [ ] Confirm five issues are closed: `gh issue list --state closed --search "19 20 21 34 39 in:title,body" ` (or verify each `gh issue view <N>`).
- [ ] Update `CHANGELOG.md` `[Unreleased]` with the STAB-001/002/005/006/019 fixes (per the repo's release convention) — done in the release plan, not here, but note the entries are owed.

**Lessons to record in `tasks/lessons.md` after execution:** (1) the installer's `PASSRESET_TEST_MODE` `return` at line 748 forces all testable logic into functions defined above it — extract-then-wire is the only TDD-able pattern here; (2) the Uninstall script has no such guard, so AST inspection (not execution) is the safe test strategy; (3) #34's `status: healthy` contract must be reconciled with the health plan's degraded/disabled semantics before tightening the success gate.
