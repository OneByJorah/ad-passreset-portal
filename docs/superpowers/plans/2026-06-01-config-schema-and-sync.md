# Implementation Plan: Config Schema Completeness + Schema-Driven Sync (Issues #27, #24, #26)

**Plan file (save to):** `docs/superpowers/plans/2026-06-01-config-schema-and-sync.md`
**Execution order across the four plans:** **1 of 4 — execute FIRST** (root dependency: the sync engine cannot manage `AdminSettings`/`Kestrel`/`LocalPolicy` keys until the schema defines them).

---

## Goal

Drive GitHub issues **#27 (STAB-008 — schema completeness)**, **#24 (STAB-010 — config sync never overwrites / adds defaults / backs up / dry-run / logs)**, and **#26 (STAB-011 — dry-run output, documentation, per-file backups, durable logs)** to **full closure** by:

1. Adding the three missing config sections (`AdminSettings`, `Kestrel`, `PasswordChangeOptions.LocalPolicy`) to `src/PassReset.Web/appsettings.schema.json` so `Test-Json` validates the full template and `ConfigSync` can manage every supported key.
2. Adding a CI schema-drift gate that fails the build when the schema omits a top-level section present in `appsettings.json`/`appsettings.Production.template.json`.
3. Extending `Sync-AppSettingsAgainstSchema` with a `Diff` (dry-run) mode, removal-counting, a per-file timestamped backup, and a durable sync log file.
4. De-gating the config-sync + drift-check blocks from the IIS-only branch so Service/Console upgrades also preserve values, add defaults, and report obsolete keys.
5. Documenting the `-ConfigSync` parameter (and the config-sync step) in the installer's comment-based help.

## Architecture

The config-sync subsystem is **schema-driven**: `appsettings.schema.json` is the single source of truth. `Get-SchemaKeyManifest` flattens the schema into leaf-key entries (path, default, obsolete flags); `Sync-AppSettingsAgainstSchema` walks that manifest against the operator's live `appsettings.Production.json`, **adding missing keys from defaults** and **never modifying existing values** (`Set-LiveValueAtPath` returns `$false` if the leaf already exists — idempotence guard at line 253). Obsolete keys (`x-passreset-obsolete`) are reported (Merge) or prompted (Review). `Test-AppSettingsSchemaDrift` is a read-only post-sync diagnostic.

This plan does **not** change the merge algorithm's core invariants — it widens the schema, adds a non-writing `Diff` mode + backup + log side-channels, and moves the invocation site out of the IIS-only gate. Because the merge engine is unchanged, existing behavior is preserved; new behavior is purely additive.

## Tech Stack

- **PowerShell 7+** — installer (`deploy/Install-PassReset.ps1`) + Pester 5 tests (`deploy/Install-PassReset.Tests.ps1`).
- **JSON Schema (draft 2020-12, Test-Json subset)** — `src/PassReset.Web/appsettings.schema.json`. Keyword set restricted to `type/required/enum/pattern/minimum/maximum/default/properties/items/additionalProperties` (per the schema's own `$comment`, D-04) for PowerShell `Test-Json` compatibility.
- **GitHub Actions** — `.github/workflows/ci.yml` (schema-drift gate + Pester run).
- **C# 13** — read-only reference (`AdminSettings.cs`, `KestrelHttpsCertOptions.cs`, `LocalPolicyOptions.cs`) for schema field parity.

## Agentic-worker sub-skill note

> Execute this plan with **`superpowers:subagent-driven-development`** in the current session: one subagent per task, each running the implementer → spec-review → code-quality loop. Every task is TDD (failing test first). Honor the issue ordering: **#27 before #24/#26**. For each medium/high-risk task, run the named regression-guard test before committing. Do not batch tasks — commit after each.

---

## File Structure

| File | Created / Modified | Responsibility |
|------|--------------------|----------------|
| `src/PassReset.Web/appsettings.schema.json` | Modified | Add `AdminSettings`, `Kestrel`, and `PasswordChangeOptions.LocalPolicy` definitions (parity with C# models + appsettings.json). |
| `deploy/schema-coverage.tests.ps1` | **Created** | Standalone Pester suite: `Test-Json` validates the full template + asserts every top-level section in `appsettings.json` is present in the schema. Reused by CI. |
| `deploy/Install-PassReset.ps1` | Modified | Add `Diff` mode + `-LogPath`/`-BackupBeforeWrite` to `Sync-AppSettingsAgainstSchema`; track removals; de-gate sync/drift from IIS branch; resolve `$ConfigSync` for all modes; per-file backup; `.PARAMETER ConfigSync` + `.DESCRIPTION` help. |
| `deploy/Install-PassReset.Tests.ps1` | Modified | Pester tests for sync add/preserve/dry-run/removal-count/backup/log + de-gated invocation + help-content assertions. |
| `.github/workflows/ci.yml` | Modified | Add a schema-drift gate step (run `deploy/schema-coverage.tests.ps1`) and a Pester step for `deploy/Install-PassReset.Tests.ps1`. |

---

## Issue #27 (STAB-008) — Schema fully reflects supported configuration

> **Sequencing:** This issue MUST complete before #24/#26. The sync/drift engine can only manage keys the schema defines.

### Task 27.1 — Schema coverage test harness (failing first)

**Files:**
- `deploy/schema-coverage.tests.ps1` (create)
- `src/PassReset.Web/appsettings.schema.json` (read-only this task; lines 15-371 = `properties`)
- `src/PassReset.Web/appsettings.json` (read-only; top-level keys incl. `AdminSettings` L194, `Kestrel` L206)

- [ ] **Step 1 — Write failing test.** Create `deploy/schema-coverage.tests.ps1`:

```powershell
# Pester 5 suite: asserts appsettings.schema.json covers every top-level
# section present in appsettings.json + that the production template validates.
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -Output Detailed"

BeforeAll {
    $repoRoot     = Split-Path -Parent $PSScriptRoot
    $script:Schema = Join-Path $repoRoot 'src/PassReset.Web/appsettings.schema.json'
    $script:AppSettings = Join-Path $repoRoot 'src/PassReset.Web/appsettings.json'
    $script:Template    = Join-Path $repoRoot 'src/PassReset.Web/appsettings.Production.template.json'

    # appsettings.json has // comments (JSONC); strip them before ConvertFrom-Json.
    function ConvertFrom-Jsonc {
        param([string]$Path)
        $lines = Get-Content $Path
        $clean = $lines | Where-Object { $_ -notmatch '^\s*//' }
        ($clean -join "`n") | ConvertFrom-Json
    }

    $script:SchemaObj  = Get-Content $script:Schema -Raw | ConvertFrom-Json
    $script:AppObj     = ConvertFrom-Jsonc -Path $script:AppSettings
}

Describe 'appsettings.schema.json covers all top-level sections' {
    It 'defines a schema property for every top-level section in appsettings.json' {
        $schemaTop = @($script:SchemaObj.properties.PSObject.Properties.Name)
        $appTop    = @($script:AppObj.PSObject.Properties.Name) | Where-Object { $_ -ne 'Logging' -and $_ -ne 'AllowedHosts' }
        $missing   = @($appTop | Where-Object { $schemaTop -notcontains $_ })
        $missing | Should -BeNullOrEmpty -Because "schema must define: $($missing -join ', ')"
    }

    It 'defines AdminSettings, Kestrel, and PasswordChangeOptions.LocalPolicy' {
        $schemaTop = @($script:SchemaObj.properties.PSObject.Properties.Name)
        $schemaTop | Should -Contain 'AdminSettings'
        $schemaTop | Should -Contain 'Kestrel'
        $script:SchemaObj.properties.PasswordChangeOptions.properties.PSObject.Properties.Name |
            Should -Contain 'LocalPolicy'
    }
}

Describe 'production template validates against the schema' {
    It 'passes Test-Json' {
        $errs = @()
        $valid = Test-Json -Path $script:Template -SchemaFile $script:Schema -ErrorVariable errs -ErrorAction SilentlyContinue
        $valid | Should -BeTrue -Because ($errs -join '; ')
    }
}
```

- [ ] **Step 2 — Run, expect FAIL** (schema lacks the three sections):
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -Output Detailed"`
  Expect the first two `It` blocks to FAIL (`AdminSettings`/`Kestrel`/`LocalPolicy` missing). The template-validation block may pass (schema has `additionalProperties:true`) — that's fine.

- [ ] **Step 3 — Minimal implementation:** none yet (the schema edits are tasks 27.2–27.3). This task only lands the harness.

- [ ] **Step 4 — Re-run:** same command. The harness must execute without runtime errors (the two coverage assertions still RED until 27.2/27.3). Confirm the failures are assertion failures, not script errors.

- [ ] **Step 5 — Commit:**
  `git add deploy/schema-coverage.tests.ps1 && git commit -m "test(installer): add schema-coverage harness asserting all sections present"`

---

### Task 27.2 — Add `PasswordChangeOptions.LocalPolicy` to schema

**Files:**
- `src/PassReset.Web/appsettings.schema.json` (insert after line 125 `"NotificationEmailTemplate"`, before the closing `}` of `PasswordChangeOptions.properties` at line 126)
- Reference: `src/PassReset.Common/LocalPolicy/LocalPolicyOptions.cs` (3 props); `appsettings.json` L73-77

- [ ] **Step 1 — Write failing test.** Append to `deploy/schema-coverage.tests.ps1`:

```powershell
Describe 'PasswordChangeOptions.LocalPolicy schema parity' {
    It 'defines BannedWordsPath, LocalPwnedPasswordsPath, MinBannedTermLength' {
        $lp = $script:SchemaObj.properties.PasswordChangeOptions.properties.LocalPolicy.properties
        $lp.PSObject.Properties.Name | Should -Contain 'BannedWordsPath'
        $lp.PSObject.Properties.Name | Should -Contain 'LocalPwnedPasswordsPath'
        $lp.PSObject.Properties.Name | Should -Contain 'MinBannedTermLength'
        $lp.MinBannedTermLength.minimum | Should -Be 1
        $lp.MinBannedTermLength.default | Should -Be 4
    }
    It 'validates a config that sets LocalPolicy.MinBannedTermLength' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "lp-$(New-Guid).json"
        @'
{ "WebSettings": { "EnableHttpsRedirect": true, "UseDebugProvider": false },
  "PasswordChangeOptions": { "UseAutomaticContext": true, "PortalLockoutThreshold": 3, "LdapPort": 636,
    "LocalPolicy": { "BannedWordsPath": null, "LocalPwnedPasswordsPath": null, "MinBannedTermLength": 4 } },
  "SmtpSettings": { "Host": "", "Port": 587 },
  "SiemSettings": { "Syslog": { "Enabled": false }, "AlertEmail": { "Enabled": false } },
  "ClientSettings": {} }
'@ | Set-Content -Path $tmp -Encoding UTF8
        try {
            Test-Json -Path $tmp -SchemaFile $script:Schema -ErrorAction SilentlyContinue | Should -BeTrue
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -Output Detailed -FullNameFilter '*LocalPolicy schema parity*'"`
  Expect FAIL (`LocalPolicy.properties` does not exist → null reference / `Should -Contain` failure).

- [ ] **Step 3 — Minimal implementation.** In `src/PassReset.Web/appsettings.schema.json`, change line 125 from:

```json
        "NotificationEmailTemplate": { "type": "string", "default": "" }
```

to (add a comma, then the `LocalPolicy` block):

```json
        "NotificationEmailTemplate": { "type": "string", "default": "" },
        "LocalPolicy": {
          "type": "object",
          "additionalProperties": true,
          "properties": {
            "BannedWordsPath":         { "type": [ "string", "null" ], "default": null },
            "LocalPwnedPasswordsPath": { "type": [ "string", "null" ], "default": null },
            "MinBannedTermLength":     { "type": "integer", "minimum": 1, "default": 4 }
          }
        }
```

- [ ] **Step 4 — Run, expect PASS** (same `-FullNameFilter` command). Then run the full file to confirm no regressions:
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -Output Detailed"`

- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/appsettings.schema.json deploy/schema-coverage.tests.ps1 && git commit -m "feat(installer): add PasswordChangeOptions.LocalPolicy to appsettings schema [#27]"`

---

### Task 27.3 — Add `AdminSettings` and `Kestrel` top-level sections to schema

**Files:**
- `src/PassReset.Web/appsettings.schema.json` (insert two new top-level properties after the `ClientSettings` block closes at line 370, before the final `}` of `properties` at line 371)
- Reference: `AdminSettings.cs` (6 props); `KestrelHttpsCertOptions.cs`; `appsettings.json` L194-216 (Kestrel uses `Endpoints.Https`)

> **Note on Kestrel shape:** `Program.cs:109` binds `KestrelHttpsCertOptions` to `Kestrel:HttpsCert`, but the shipped `appsettings.json`/template use the framework-standard `Kestrel.Endpoints.Https` shape (Url + Certificate.Path/Password). The schema must model **what ConfigSync walks in the live file** = `Endpoints.Https`. Keep `additionalProperties:true` on `Kestrel` so a `HttpsCert` sub-section is also tolerated.

- [ ] **Step 1 — Write failing test.** Append to `deploy/schema-coverage.tests.ps1`:

```powershell
Describe 'AdminSettings + Kestrel schema parity' {
    It 'AdminSettings defines all 6 C# properties with correct defaults' {
        $a = $script:SchemaObj.properties.AdminSettings.properties
        $a.Enabled.default       | Should -Be $false
        $a.LoopbackPort.default  | Should -Be 5010
        $a.LoopbackPort.minimum  | Should -Be 1024
        $a.LoopbackPort.maximum  | Should -Be 65535
        foreach ($p in 'KeyStorePath','DataProtectionCertThumbprint','AppSettingsFilePath','SecretsFilePath') {
            $a.PSObject.Properties.Name | Should -Contain $p
        }
    }
    It 'Kestrel defines Endpoints.Https.Url and Certificate.Path/Password' {
        $https = $script:SchemaObj.properties.Kestrel.properties.Endpoints.properties.Https.properties
        $https.PSObject.Properties.Name | Should -Contain 'Url'
        $https.Certificate.properties.PSObject.Properties.Name | Should -Contain 'Path'
        $https.Certificate.properties.PSObject.Properties.Name | Should -Contain 'Password'
    }
    It 'validates a config carrying full AdminSettings + Kestrel sections' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "ak-$(New-Guid).json"
        @'
{ "WebSettings": { "EnableHttpsRedirect": true, "UseDebugProvider": false },
  "PasswordChangeOptions": { "UseAutomaticContext": true, "PortalLockoutThreshold": 3, "LdapPort": 636 },
  "SmtpSettings": { "Host": "", "Port": 587 },
  "SiemSettings": { "Syslog": { "Enabled": false }, "AlertEmail": { "Enabled": false } },
  "ClientSettings": {},
  "AdminSettings": { "Enabled": false, "LoopbackPort": 5010, "KeyStorePath": null,
    "DataProtectionCertThumbprint": null, "AppSettingsFilePath": null, "SecretsFilePath": null },
  "Kestrel": { "Endpoints": { "Https": { "Url": "https://localhost:5001",
    "Certificate": { "Path": null, "Password": null } } } } }
'@ | Set-Content -Path $tmp -Encoding UTF8
        try {
            Test-Json -Path $tmp -SchemaFile $script:Schema -ErrorAction SilentlyContinue | Should -BeTrue
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -Output Detailed -FullNameFilter '*AdminSettings + Kestrel*'"`
  Expect FAIL (sections absent).

- [ ] **Step 3 — Minimal implementation.** In `src/PassReset.Web/appsettings.schema.json`, change the `ClientSettings` block's closing lines (368-370):

```json
        }
      }
    }
  }
}
```

to (add a comma after the `ClientSettings` close, then the two new sections):

```json
        }
      }
    },

    "AdminSettings": {
      "type": "object",
      "additionalProperties": true,
      "properties": {
        "Enabled": { "type": "boolean", "default": false },
        "LoopbackPort": { "type": "integer", "minimum": 1024, "maximum": 65535, "default": 5010 },
        "KeyStorePath":                 { "type": [ "string", "null" ], "default": null },
        "DataProtectionCertThumbprint": { "type": [ "string", "null" ], "default": null },
        "AppSettingsFilePath":          { "type": [ "string", "null" ], "default": null },
        "SecretsFilePath":              { "type": [ "string", "null" ], "default": null }
      }
    },

    "Kestrel": {
      "type": "object",
      "additionalProperties": true,
      "properties": {
        "Endpoints": {
          "type": "object",
          "additionalProperties": true,
          "properties": {
            "Https": {
              "type": "object",
              "additionalProperties": true,
              "properties": {
                "Url": { "type": "string", "default": "https://localhost:5001" },
                "Certificate": {
                  "type": "object",
                  "additionalProperties": true,
                  "properties": {
                    "Path":     { "type": [ "string", "null" ], "default": null },
                    "Password": { "type": [ "string", "null" ], "default": null }
                  }
                }
              }
            }
          }
        }
      }
    }
  }
}
```

> **Regression note (low risk):** Adding top-level keys with `additionalProperties:true` everywhere is purely additive — no existing key constraints change. The `Get-SchemaKeyManifest` recursion (line 174: `$isObj = ($node.type -eq 'object') -or ($null -ne $node.properties)`) will now emit new leaf entries for these sections, which is the intended effect for #24.

- [ ] **Step 4 — Run, expect PASS:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -Output Detailed"`
  All `Describe` blocks GREEN (including 27.1's coverage assertions). Also confirm the schema is still valid JSON:
  `pwsh -NoProfile -Command "Get-Content src/PassReset.Web/appsettings.schema.json -Raw | ConvertFrom-Json | Out-Null; 'parse-ok'"`

- [ ] **Step 5 — Commit:**
  `git add src/PassReset.Web/appsettings.schema.json deploy/schema-coverage.tests.ps1 && git commit -m "feat(installer): add AdminSettings + Kestrel sections to appsettings schema [#27]"`

---

### Task 27.4 — CI schema-drift gate

**Files:**
- `.github/workflows/ci.yml` (insert a new step after line 54, after the existing "Validate appsettings.Production.template.json against schema" step, before "Build solution" at line 56)

- [ ] **Step 1 — Write failing test (local reproduction of the CI gate).** Confirm the harness from 27.1 is the gate. First prove the gate *catches* drift by temporarily removing `AdminSettings` from the schema in a scratch copy and asserting failure — run this one-liner (no file committed; it's the proof the gate works):

```powershell
pwsh -NoProfile -Command "$s = Get-Content src/PassReset.Web/appsettings.schema.json -Raw | ConvertFrom-Json; $s.properties.PSObject.Properties.Remove('AdminSettings'); $tmp = New-TemporaryFile; ($s | ConvertTo-Json -Depth 40) | Set-Content $tmp; $app = (Get-Content src/PassReset.Web/appsettings.json | Where-Object { $_ -notmatch '^\s*//' }) -join \"`n\" | ConvertFrom-Json; $top = @($s.properties.PSObject.Properties.Name); $missing = @($app.PSObject.Properties.Name | Where-Object { $_ -notin @('Logging','AllowedHosts') -and $_ -notin $top }); if ($missing) { Write-Host \"GATE WORKS - would fail on: $($missing -join ',')\"; exit 0 } else { Write-Error 'gate did not detect drift'; exit 1 }; Remove-Item $tmp"
```
Expect output `GATE WORKS - would fail on: AdminSettings` (exit 0).

- [ ] **Step 2 — Run, expect the gate to currently be ABSENT in CI.** Confirm there is no Pester/schema-drift step yet:
  `pwsh -NoProfile -Command "if ((Get-Content .github/workflows/ci.yml -Raw) -match 'schema-coverage') { Write-Error 'already present' } else { Write-Host 'absent - add it' }"`
  Expect `absent - add it`.

- [ ] **Step 3 — Minimal implementation.** In `.github/workflows/ci.yml`, after line 54 (`          Write-Host 'Schema validation passed.'`), insert:

```yaml

      - name: Schema-drift gate (schema covers every appsettings.json section)
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          if (-not (Get-Module -ListAvailable Pester | Where-Object { $_.Version -ge [version]'5.0.0' })) {
            Install-Module Pester -MinimumVersion 5.5.0 -Force -Scope CurrentUser -SkipPublisherCheck
          }
          $r = Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -PassThru -Output Detailed
          if ($r.FailedCount -gt 0) {
            Write-Host "::error::appsettings.schema.json is missing sections present in appsettings.json (STAB-008 drift gate). $($r.FailedCount) check(s) failed."
            exit 1
          }
          Write-Host 'Schema-drift gate passed: schema covers all config sections.'
```

> **Regression note (low risk):** This step runs on `windows-latest` where `Test-Json` and Pester are available. It only adds a gate; it cannot change build/test outcomes for code. If Pester install is flaky, the gate fails loudly (intended) rather than silently passing.

- [ ] **Step 4 — Validate workflow YAML parses.** Run:
  `pwsh -NoProfile -Command "Get-Content .github/workflows/ci.yml -Raw | Out-Null; (Select-String -Path .github/workflows/ci.yml -Pattern 'schema-coverage' -Quiet) }"`
  Expect `True`. (Optional, if `act`/`actionlint` available: `actionlint .github/workflows/ci.yml`.)

- [ ] **Step 5 — Commit:**
  `git add .github/workflows/ci.yml && git commit -m "ci: add schema-drift gate ensuring schema covers all config sections [#27]"`

---

### Task 27.5 — Verify #27 acceptance criteria and close issue

**Files:** none (verification + GitHub).

- [ ] **Step 1 — Run the full coverage suite** and confirm all green:
  `pwsh -NoProfile -Command "$r = Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -PassThru -Output Detailed; if ($r.FailedCount -ne 0) { exit 1 }"`

- [ ] **Step 2 — Acceptance-criteria checklist** (assert each against the work above):
  - [x] *Schema fully reflects current supported configuration* → `AdminSettings`, `Kestrel`, `PasswordChangeOptions.LocalPolicy` now in schema (27.2, 27.3); coverage test enforces parity with `appsettings.json`.
  - [x] *Config sync uses the schema to detect obsolete keys* → manual proof: add a temporary `x-passreset-obsolete` flag to `AdminSettings.LoopbackPort`, run drift check on a config containing it, confirm it's reported, then revert. Run:
    `pwsh -NoProfile -Command ". deploy/Install-PassReset.ps1; $s = Get-Content src/PassReset.Web/appsettings.schema.json -Raw | ConvertFrom-Json; $s.properties.AdminSettings.properties.LoopbackPort | Add-Member -NotePropertyName 'x-passreset-obsolete' -NotePropertyValue $true; $st = New-TemporaryFile; ($s|ConvertTo-Json -Depth 40)|Set-Content $st; $cfg = New-TemporaryFile; '{ \"AdminSettings\": { \"LoopbackPort\": 5010 } }' | Set-Content $cfg; $d = Test-AppSettingsSchemaDrift -SchemaPath $st -ConfigPath $cfg; if ($d.Obsolete.Path -contains 'AdminSettings:LoopbackPort') { 'OBSOLETE-DETECTED' } else { throw 'not detected' }"`
    (sets `PASSRESET_TEST_MODE` is not needed here because we dot-source after the guard; if the flow runs, prefix `$env:PASSRESET_TEST_MODE='1';`). Expect `OBSOLETE-DETECTED`.
  - [x] *Schema is kept in sync with releases* → CI schema-drift gate added (27.4).

- [ ] **Step 3 — Close the issue:**
  `gh issue close 27 --reason completed --comment "STAB-008 closed: schema now defines AdminSettings, Kestrel, and PasswordChangeOptions.LocalPolicy (full parity with appsettings.json + C# models). Added deploy/schema-coverage.tests.ps1 and a CI schema-drift gate (ci.yml) that fails the build if the schema omits any config section. Verified Test-Json validates the full production template and that obsolete-key detection works for the new sections."`

---

## Issue #24 (STAB-010) — Sync: never overwrite, add defaults, dry-run, backup, logs, all hosting modes

> **Depends on #27** (schema must define `AdminSettings`/`Kestrel`/`LocalPolicy` so sync manages them). All sync edits below are to `Sync-AppSettingsAgainstSchema` (lines 281-362) and the invocation site.

### Task 24.1 — Regression-guard: lock in current sync invariants (add + never-overwrite)

**Files:**
- `deploy/Install-PassReset.Tests.ps1` (append after line 50; current file ends at line 51)

> **Why first:** This is the regression guard for the medium-risk de-gating and signature changes that follow. It must stay green through every later task.

- [ ] **Step 1 — Write the test.** Append to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'Sync-AppSettingsAgainstSchema: core invariants (regression guard)' {
    BeforeAll {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $script:RealSchema = Join-Path $repoRoot 'src/PassReset.Web/appsettings.schema.json'
    }
    BeforeEach {
        $script:cfg = Join-Path ([IO.Path]::GetTempPath()) "sync-$(New-Guid).json"
    }
    AfterEach {
        Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'sync-*' -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'adds a missing key from its schema default (Merge mode)' {
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true } }' | Set-Content $script:cfg -Encoding UTF8
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Merge'
        $after = Get-Content $script:cfg -Raw | ConvertFrom-Json
        $after.PasswordChangeOptions.PortalLockoutThreshold | Should -Be 3
    }

    It 'never overwrites an existing non-default value (Merge mode)' {
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true, "PortalLockoutThreshold": 99 } }' |
            Set-Content $script:cfg -Encoding UTF8
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Merge'
        $after = Get-Content $script:cfg -Raw | ConvertFrom-Json
        $after.PasswordChangeOptions.PortalLockoutThreshold | Should -Be 99
    }
}
```

- [ ] **Step 2 — Run, expect PASS** (these invariants already hold on HEAD — this is a *characterization* test, not a red test):
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*core invariants*'"`
  If either FAILS, STOP — the sync engine does not behave as documented; re-plan before proceeding.

- [ ] **Step 3 — Implementation:** none (guard only).

- [ ] **Step 4 — Re-run:** confirm GREEN.

- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.Tests.ps1 && git commit -m "test(installer): regression-guard sync add + never-overwrite invariants [#24]"`

---

### Task 24.2 — Add `Diff` (dry-run) mode + removal counter to `Sync-AppSettingsAgainstSchema`

**Files:**
- `deploy/Install-PassReset.ps1` lines 281-362 (function body; `ValidateSet` at 286, removal site at 317, write at 352-357)
- `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** Append to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'Sync-AppSettingsAgainstSchema: Diff (dry-run) + removal counter' {
    BeforeAll { $script:RealSchema = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PassReset.Web/appsettings.schema.json' }
    BeforeEach { $script:cfg = Join-Path ([IO.Path]::GetTempPath()) "diff-$(New-Guid).json" }
    AfterEach  { Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'diff-*' -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue }

    It 'Diff mode does NOT write the file' {
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true } }' | Set-Content $script:cfg -Encoding UTF8
        $before = Get-Content $script:cfg -Raw
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Diff'
        (Get-Content $script:cfg -Raw) | Should -BeExactly $before
    }

    It 'Diff mode emits a would-add line for each missing key' {
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true } }' | Set-Content $script:cfg -Encoding UTF8
        $out = Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Diff' 6>&1 | Out-String
        $out | Should -Match 'would add .*PortalLockoutThreshold'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Diff (dry-run)*'"`
  Expect FAIL: `ValidateSet` rejects `'Diff'` → the function throws a parameter-binding error.

- [ ] **Step 3 — Minimal implementation.** Edit `deploy/Install-PassReset.ps1`:

  (a) Line 286 — widen the ValidateSet:
  ```powershell
        [Parameter(Mandatory)] [ValidateSet('Merge','Review','None','Diff')] [string] $Mode
  ```

  (b) Inside the `foreach ($entry in $manifest)` loop, initialize a removal counter and a `Diff` short-circuit. Replace lines 305-308:
  ```powershell
    $additions = @()
    $obsoleteFound = @()
    $modified = $false
  ```
  with:
  ```powershell
    $additions    = @()
    $obsoleteFound = @()
    $removedCount = 0
    $modified     = $false
    $isDryRun     = ($Mode -eq 'Diff')
  ```

  (c) For obsolete-key removal (the `Remove-LiveValueAtPath` success at line 318-319), add the counter. Replace lines 317-320:
  ```powershell
                        if (Remove-LiveValueAtPath -Config $live -Path $entry.Path) {
                            $modified = $true
                            Write-Ok "  - Removed obsolete: $($entry.Path)"
                        }
  ```
  with:
  ```powershell
                        if (Remove-LiveValueAtPath -Config $live -Path $entry.Path) {
                            $modified = $true
                            $removedCount++
                            Write-Ok "  - Removed obsolete: $($entry.Path)"
                        }
  ```

  (d) For the addition branch, make `Diff` mode log "would add" instead of mutating. Replace lines 335-345:
  ```powershell
            if ($Mode -eq 'Review') {
                $defaultDisplay = if ($entry.Default -is [array]) { '[' + (($entry.Default | ForEach-Object { "`"$_`"" }) -join ',') + ']' } else { "$($entry.Default)" }
                $reply = Read-Host "  Add '$($entry.Path)' with default = $defaultDisplay? [Y/N] [Y]"
                if ($reply -and $reply -notmatch '^[Yy]' -and $reply -notmatch '^$') { continue }
            }
            try {
                if (Set-LiveValueAtPath -Config $live -Path $entry.Path -Value $entry.Default) {
                    $modified = $true
                    $additions += $entry
                    Write-Ok "  + $($entry.Path) = $($entry.Default)"
                }
            } catch {
                Write-Warn "Could not add '$($entry.Path)': $($_.Exception.Message)"
            }
  ```
  with:
  ```powershell
            if ($isDryRun) {
                $additions += $entry
                Write-Ok "  would add $($entry.Path) = $($entry.Default)"
                continue
            }
            if ($Mode -eq 'Review') {
                $defaultDisplay = if ($entry.Default -is [array]) { '[' + (($entry.Default | ForEach-Object { "`"$_`"" }) -join ',') + ']' } else { "$($entry.Default)" }
                $reply = Read-Host "  Add '$($entry.Path)' with default = $defaultDisplay? [Y/N] [Y]"
                if ($reply -and $reply -notmatch '^[Yy]' -and $reply -notmatch '^$') { continue }
            }
            try {
                if (Set-LiveValueAtPath -Config $live -Path $entry.Path -Value $entry.Default) {
                    $modified = $true
                    $additions += $entry
                    Write-Ok "  + $($entry.Path) = $($entry.Default)"
                }
            } catch {
                Write-Warn "Could not add '$($entry.Path)': $($_.Exception.Message)"
            }
  ```

  (e) Guard the file write against dry-run and add the removal summary. Replace lines 352-357:
  ```powershell
    if ($modified) {
        $live | ConvertTo-Json -Depth 32 | Set-Content -Path $ConfigPath -Encoding UTF8 -NoNewline
        Write-Ok "Wrote $($additions.Count) addition(s) to $ConfigPath"
    } else {
        Write-Ok 'Config is in sync with schema; no changes written.'
    }
  ```
  with:
  ```powershell
    if ($isDryRun) {
        Write-Ok "Dry-run (-ConfigSync Diff): $($additions.Count) key(s) would be added, $($obsoleteFound.Count) obsolete key(s) present. No file written."
        return
    }
    if ($modified) {
        $live | ConvertTo-Json -Depth 32 | Set-Content -Path $ConfigPath -Encoding UTF8 -NoNewline
        Write-Ok "Sync summary: $($additions.Count) added, $removedCount removed. Wrote $ConfigPath"
    } else {
        Write-Ok 'Config is in sync with schema; no changes written.'
    }
  ```

> **Regression note (medium risk):** Adding `'Diff'` to the ValidateSet and an early `return` is additive — `Merge`/`Review`/`None` paths are unchanged except the write log line now reports `$removedCount` (always 0 in Merge since Merge never removes). The 24.1 regression guard must remain green.

- [ ] **Step 4 — Run, expect PASS:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Diff (dry-run)*'"`
  Then re-run the 24.1 guard: `... -FullNameFilter '*core invariants*'` — must still be GREEN.

- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): add Diff dry-run mode + removal counter to config sync [#24]"`

---

### Task 24.3 — Per-file timestamped backup + durable sync log

**Files:**
- `deploy/Install-PassReset.ps1` lines 281-362 (add `-LogPath`/`-BackupBeforeWrite` params + backup/log logic)
- `deploy/Install-PassReset.Tests.ps1` (append)

- [ ] **Step 1 — Write failing test.** Append to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'Sync-AppSettingsAgainstSchema: per-file backup + sync log' {
    BeforeAll { $script:RealSchema = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PassReset.Web/appsettings.schema.json' }
    BeforeEach {
        $script:dir = Join-Path ([IO.Path]::GetTempPath()) "bk-$(New-Guid)"
        New-Item -ItemType Directory -Path $script:dir | Out-Null
        $script:cfg = Join-Path $script:dir 'appsettings.Production.json'
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true } }' | Set-Content $script:cfg -Encoding UTF8
    }
    AfterEach { Remove-Item $script:dir -Recurse -Force -EA SilentlyContinue }

    It 'creates a timestamped .bak before writing (Merge mode mutates)' {
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Merge'
        (Get-ChildItem $script:dir -Filter 'appsettings.Production.json_*.bak').Count | Should -BeGreaterThan 0
    }

    It 'does NOT create a backup in Diff mode (no write)' {
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Diff'
        (Get-ChildItem $script:dir -Filter '*.bak').Count | Should -Be 0
    }

    It 'writes a durable sync log listing additions' {
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Merge'
        $log = Get-ChildItem $script:dir -Filter 'appsettings.Production_sync-*.log' | Select-Object -First 1
        $log | Should -Not -BeNullOrEmpty
        (Get-Content $log.FullName -Raw) | Should -Match 'PortalLockoutThreshold'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*per-file backup + sync log*'"`
  Expect FAIL: no `.bak` / no `.log` produced.

- [ ] **Step 3 — Minimal implementation.** Edit `deploy/Install-PassReset.ps1`:

  (a) Add two parameters. After line 286 (`$Mode` param), extend the `param(...)` block — change:
  ```powershell
        [Parameter(Mandatory)] [ValidateSet('Merge','Review','None','Diff')] [string] $Mode
    )
  ```
  to:
  ```powershell
        [Parameter(Mandatory)] [ValidateSet('Merge','Review','None','Diff')] [string] $Mode,
        [string] $LogPath = ''
    )
  ```

  (b) Initialize the log path right after the `$isDryRun` line added in 24.2:
  ```powershell
    $isDryRun = ($Mode -eq 'Diff')
  ```
  becomes:
  ```powershell
    $isDryRun = ($Mode -eq 'Diff')
    if (-not $LogPath) {
        $stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
        $base    = [IO.Path]::Combine([IO.Path]::GetDirectoryName($ConfigPath),
                       [IO.Path]::GetFileNameWithoutExtension($ConfigPath))
        $LogPath = "${base}_sync-${stamp}.log"
    }
    function script:Add-SyncLog { param([string]$Line) Add-Content -Path $LogPath -Value "$(Get-Date -Format o)  $Line" -Encoding UTF8 }
    Add-SyncLog "Config sync started: mode=$Mode, config=$ConfigPath"
  ```

  (c) Log additions: in the addition success branch (the `Write-Ok "  + ..."` line), add a log call. Change:
  ```powershell
                    Write-Ok "  + $($entry.Path) = $($entry.Default)"
  ```
  to:
  ```powershell
                    Write-Ok "  + $($entry.Path) = $($entry.Default)"
                    Add-SyncLog "ADD  $($entry.Path) = $($entry.Default)"
  ```
  And in the `Diff` "would add" branch:
  ```powershell
                Write-Ok "  would add $($entry.Path) = $($entry.Default)"
                continue
  ```
  to:
  ```powershell
                Write-Ok "  would add $($entry.Path) = $($entry.Default)"
                Add-SyncLog "WOULD-ADD  $($entry.Path) = $($entry.Default)"
                continue
  ```
  And for obsolete removal success:
  ```powershell
                            $removedCount++
                            Write-Ok "  - Removed obsolete: $($entry.Path)"
  ```
  to:
  ```powershell
                            $removedCount++
                            Write-Ok "  - Removed obsolete: $($entry.Path)"
                            Add-SyncLog "REMOVE  $($entry.Path)"
  ```

  (d) Create the per-file backup immediately before the file write. In the final block (after 24.2's edits), change:
  ```powershell
    if ($modified) {
        $live | ConvertTo-Json -Depth 32 | Set-Content -Path $ConfigPath -Encoding UTF8 -NoNewline
        Write-Ok "Sync summary: $($additions.Count) added, $removedCount removed. Wrote $ConfigPath"
    } else {
        Write-Ok 'Config is in sync with schema; no changes written.'
    }
  ```
  to:
  ```powershell
    if ($modified) {
        $backupPath = "${ConfigPath}_$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
        Copy-Item -Path $ConfigPath -Destination $backupPath -Force
        Write-Ok "Backup before sync: $backupPath"
        Add-SyncLog "BACKUP  $backupPath"
        $live | ConvertTo-Json -Depth 32 | Set-Content -Path $ConfigPath -Encoding UTF8 -NoNewline
        Write-Ok "Sync summary: $($additions.Count) added, $removedCount removed. Wrote $ConfigPath"
        Write-Ok "Sync log: $LogPath"
        Add-SyncLog "Sync summary: $($additions.Count) added, $removedCount removed."
    } else {
        Write-Ok 'Config is in sync with schema; no changes written.'
        Add-SyncLog 'No changes.'
    }
  ```
  And update the `Diff` early-return to also note the log:
  ```powershell
    if ($isDryRun) {
        Write-Ok "Dry-run (-ConfigSync Diff): $($additions.Count) key(s) would be added, $($obsoleteFound.Count) obsolete key(s) present. No file written."
        Write-Ok "Sync log: $LogPath"
        return
    }
  ```

> **Regression note (medium risk):** The backup is taken **of the live file just before overwrite**, so it captures the pre-sync state for rollback — closing #24's "backup before modification" gap that the pre-startup full-folder backup missed. The log is append-only and never blocks the sync. `Add-SyncLog` is `script:`-scoped to avoid leaking into the dot-sourced test session across calls; verify no name collision (grep confirms `Add-SyncLog` is new).

- [ ] **Step 4 — Run, expect PASS:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*per-file backup + sync log*'"`
  Re-run guards: `... -FullNameFilter '*core invariants*'` and `... -FullNameFilter '*Diff (dry-run)*'` — all GREEN.

- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "feat(installer): per-file backup + durable sync log in config sync [#24]"`

---

### Task 24.4 — Resolve `$ConfigSync` mode for all hosting modes (not just IIS upgrades)

**Files:**
- `deploy/Install-PassReset.ps1` lines 1125-1145 (mode resolution; currently keys off `$siteExists`, only true under IIS)
- `deploy/Install-PassReset.Tests.ps1` (append — extract resolution into a testable helper)

> **Root cause (per gap analysis):** mode resolution at 1126-1145 only prompts for upgrades via `$siteExists`, which is `$false` for Service/Console (set only when IIS available, line 965). Service/Console upgrades therefore default to `'None'` and never sync.

- [ ] **Step 1 — Write failing test.** Append to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'Resolve-ConfigSyncMode: hosting-mode aware resolution' {
    It 'returns Merge when -Force regardless of mode' {
        Resolve-ConfigSyncMode -Requested '' -Force $true -IsUpgrade $false -Interactive $false | Should -Be 'Merge'
    }
    It 'returns None on fresh install (no upgrade)' {
        Resolve-ConfigSyncMode -Requested '' -Force $false -IsUpgrade $false -Interactive $false | Should -Be 'Merge' -Because 'non-interactive upgrade=false still safe'
    }
    It 'honors an explicit -ConfigSync value' {
        Resolve-ConfigSyncMode -Requested 'Diff' -Force $false -IsUpgrade $true -Interactive $true | Should -Be 'Diff'
    }
    It 'returns Merge on a non-interactive upgrade (Service/Console unattended)' {
        Resolve-ConfigSyncMode -Requested '' -Force $false -IsUpgrade $true -Interactive $false | Should -Be 'Merge'
    }
}
```

> Adjust the "fresh install" expectation: a fresh, non-interactive, non-forced install should be `None`. Use this corrected test instead (replace the second `It`):

```powershell
    It 'returns None on a fresh non-interactive install' {
        Resolve-ConfigSyncMode -Requested '' -Force $false -IsUpgrade $false -Interactive $false | Should -Be 'None'
    }
```

- [ ] **Step 2 — Run, expect FAIL:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Resolve-ConfigSyncMode*'"`
  Expect FAIL: `Resolve-ConfigSyncMode` does not exist.

- [ ] **Step 3 — Minimal implementation.** Add the helper near the other config-sync helpers (after `Sync-AppSettingsAgainstSchema`, i.e. after line 362 / before the drift-check comment at line 364):

```powershell
function Resolve-ConfigSyncMode {
    [CmdletBinding()]
    param(
        [string] $Requested,
        [bool]   $Force,
        [bool]   $IsUpgrade,
        [bool]   $Interactive
    )
    if ($Requested) { return $Requested }
    if ($Force)     { return 'Merge' }
    if (-not $IsUpgrade) { return 'None' }          # fresh install: template copied verbatim
    if (-not $Interactive) { return 'Merge' }       # unattended upgrade (Service/Console/CI): safe additive
    $reply = Read-Host '  Config sync: [M]erge additions / [R]eview each / [D]ry-run diff / [S]kip? [M]'
    switch -Regex ($reply) {
        '^[Rr]' { 'Review' }
        '^[Dd]' { 'Diff' }
        '^[Ss]' { 'None' }
        default { 'Merge' }
    }
}
```

  Then replace the inline resolution at lines 1125-1145:
  ```powershell
  Write-Step 'Resolving config sync mode'
  if (-not $ConfigSync) {
      if ($Force) {
          $ConfigSync = 'Merge'
          Write-Ok "-Force specified - defaulting to -ConfigSync Merge"
      } elseif ($siteExists) {
          # Upgrade detected, interactive session — prompt per D-13.
          $reply = Read-Host '  Config sync: [M]erge additions / [R]eview each / [S]kip? [M]'
          $ConfigSync = switch -Regex ($reply) {
              '^[Rr]' { 'Review' }
              '^[Ss]' { 'None' }
              default { 'Merge' }
          }
          Write-Ok "Config sync mode: $ConfigSync"
      } else {
          # Fresh install — template was just copied verbatim; nothing to sync.
          $ConfigSync = 'None'
      }
  } else {
      Write-Ok "Config sync mode (from -ConfigSync param): $ConfigSync"
  }
  ```
  with:
  ```powershell
  Write-Step 'Resolving config sync mode'
  # Upgrade detection works across all hosting modes: an existing live config in
  # the target folder (IIS site OR Service/Console robocopy target) means upgrade.
  $prodConfigForResolve = Join-Path $PhysicalPath 'appsettings.Production.json'
  $isUpgrade   = $siteExists -or (Test-Path $prodConfigForResolve)
  $interactive = -not $Force -and -not [System.Console]::IsInputRedirected
  $ConfigSync  = Resolve-ConfigSyncMode -Requested $ConfigSync -Force ([bool]$Force) `
                     -IsUpgrade $isUpgrade -Interactive $interactive
  Write-Ok "Config sync mode: $ConfigSync"
  ```

> **Regression note (medium risk):** IIS-upgrade behavior is preserved — `$siteExists` still drives `$isUpgrade` on IIS, and `-Force`→`Merge` is unchanged. The new path additionally treats a Service/Console target that already has `appsettings.Production.json` as an upgrade. `[System.Console]::IsInputRedirected` keeps CI/unattended runs from blocking on `Read-Host`. Guard: the 24.1 invariants and Service-mode tests (24.6) must pass.

- [ ] **Step 4 — Run, expect PASS:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Resolve-ConfigSyncMode*'"`

- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "fix(installer): resolve config-sync mode for all hosting modes, not just IIS [#24]"`

---

### Task 24.5 — De-gate config sync + drift check from the IIS-only block

**Files:**
- `deploy/Install-PassReset.ps1` lines 1735-1798 (move "9b. Config sync" + "9c. Schema drift check" outside the `if ($HostingMode -eq 'IIS')` block that closes at 1798)

> **Root cause:** blocks 9b/9c live inside the IIS branch (opened at 1147). Service/Console never reach them. Move them below line 1820 (after the `elseif` chain) so they run for every mode.

- [ ] **Step 1 — Write failing test.** Append to `deploy/Install-PassReset.Tests.ps1`:

```powershell
Describe 'Config sync is de-gated from the IIS-only block' {
    It 'invokes Sync/Drift outside the IIS branch (static check)' {
        $src   = Get-Content (Join-Path $PSScriptRoot 'Install-PassReset.ps1') -Raw
        $lines = $src -split "`r?`n"

        # Find the line index of the IIS block close marker.
        $iisClose = ($lines | Select-String -Pattern "end if \(\`$HostingMode -eq 'IIS'\)").LineNumber
        $syncCall = ($lines | Select-String -Pattern 'Sync-AppSettingsAgainstSchema `').LineNumber | Select-Object -Last 1
        $driftCall = ($lines | Select-String -Pattern 'Test-AppSettingsSchemaDrift `').LineNumber | Select-Object -Last 1

        $iisClose  | Should -Not -BeNullOrEmpty
        $syncCall  | Should -BeGreaterThan $iisClose -Because 'sync must run after the IIS block closes (all modes)'
        $driftCall | Should -BeGreaterThan $iisClose -Because 'drift check must run after the IIS block closes (all modes)'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*de-gated*'"`
  Expect FAIL: sync/drift invocations are *above* the IIS close marker (line 1798).

- [ ] **Step 3 — Minimal implementation.** Cut blocks **9b** (lines 1735-1749) and **9c** (lines 1751-1797) out of the IIS branch, and re-insert them **after** the hosting-mode `elseif` chain closes (after line 1820, before `# ─── Done ───` at line 1822). Concretely:

  (a) Delete lines 1735-1797 from inside the IIS block (the two `# ─── 9b` / `# ─── 9c` sections), so line 1798 `} # end if ($HostingMode -eq 'IIS')` now immediately follows the start-site `catch` block at 1733.

  (b) After line 1820 (the `elseif ($HostingMode -eq 'Console')` block's closing `}`), insert a mode-agnostic sync+drift section:

  ```powershell

  # ─── 10. Config sync + drift check (ALL hosting modes — STAB-010/STAB-012) ────
  # Runs for IIS, Service, and Console. $ConfigSync was resolved earlier
  # (Resolve-ConfigSyncMode). On Service/Console, $prodConfig points at the
  # robocopy target. Sync adds missing keys from schema defaults, never
  # overwriting existing values (D-13); the drift check is read-only.
  $prodConfig = Join-Path $PhysicalPath 'appsettings.Production.json'
  $schemaFile = Join-Path $PhysicalPath 'appsettings.schema.json'

  if (Test-Path $prodConfig) {
      Write-Step 'Syncing appsettings.Production.json against schema'
      Sync-AppSettingsAgainstSchema -SchemaPath $schemaFile -ConfigPath $prodConfig -Mode $ConfigSync

      Write-Step 'Checking appsettings.Production.json for schema drift'
      $drift = Test-AppSettingsSchemaDrift -SchemaPath $schemaFile -ConfigPath $prodConfig
      if (-not $drift.Skipped) {
          $hasDrift = $false
          if ($drift.Missing.Count -gt 0) {
              $hasDrift = $true
              Write-Warn "Schema drift: $($drift.Missing.Count) required key(s) still missing from ${prodConfig}:"
              foreach ($m in $drift.Missing) {
                  $defaultHint = if ($m.HasDefault) { " (schema default: $($m.Default))" } else { ' (no default in schema; manual entry required)' }
                  Write-Host "    - $($m.Path)$defaultHint" -ForegroundColor Yellow
              }
              if ($ConfigSync -eq 'None') { Write-Warn 'Re-run with -ConfigSync Merge to add missing keys automatically.' }
          }
          if ($drift.Obsolete.Count -gt 0) {
              $hasDrift = $true
              Write-Warn "Schema drift: $($drift.Obsolete.Count) obsolete key(s) present in ${prodConfig}:"
              foreach ($o in $drift.Obsolete) { Write-Host "    - $($o.Path) (obsolete since v$($o.ObsoleteSince))" -ForegroundColor Yellow }
              Write-Warn 'Re-run with -ConfigSync Review to remove obsolete keys interactively.'
          }
          if ($drift.Unknown.Count -gt 0) {
              Write-Host "  [i] $($drift.Unknown.Count) unknown top-level key(s) in $prodConfig (allowed; informational only):" -ForegroundColor DarkGray
              foreach ($u in $drift.Unknown) { Write-Host "    - $u" -ForegroundColor DarkGray }
          }
          if (-not $hasDrift) { Write-Ok 'No schema drift detected.' }
      }
  }
  ```

> **Regression note (HIGH risk — this is the riskiest change in the plan).** Moving 9b/9c out of the IIS gate changes when they execute on IIS (now after the `elseif` chain rather than just before the IIS block closes). Because both blocks only *read/append* against `$prodConfig` and the IIS branch already set `$prodConfig`/started the site, behavior on IIS is unchanged in ordering relative to site start. What could break: (1) `$prodConfig`/`$schemaFile` must be defined in the new scope — the snippet re-derives both from `$PhysicalPath`, so no reliance on IIS-branch locals; (2) the old IIS code gated sync on `$siteExists -and (Test-Path $prodConfig)` — the new code gates only on `Test-Path $prodConfig`, which is correct (fresh installs have a freshly-copied template config, and `$ConfigSync` is `None` there, so sync is a no-op). **Required guards before commit:** run the FULL Pester suite (24.1 invariants, 24.2, 24.3, 24.4) plus the new de-gate test; all must be green.

- [ ] **Step 4 — Run, expect PASS:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*de-gated*'"`
  Then full-suite regression: `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"` — all GREEN. Also confirm the installer still parses:
  `pwsh -NoProfile -Command "$env:PASSRESET_TEST_MODE='1'; . deploy/Install-PassReset.ps1; 'dot-source-ok'"`

- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "fix(installer): run config sync + drift check on all hosting modes [#24]"`

---

### Task 24.6 — Service/Console upgrade invokes sync (behavioral test)

**Files:**
- `deploy/Install-PassReset.Tests.ps1` (append — behavioral test exercising the de-gated path via `Resolve-ConfigSyncMode` + `Sync-AppSettingsAgainstSchema` directly, since a real Service install needs admin)

- [ ] **Step 1 — Write failing/characterization test.** Append:

```powershell
Describe 'Service/Console upgrade path syncs config (behavioral)' {
    BeforeAll { $script:RealSchema = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PassReset.Web/appsettings.schema.json' }
    BeforeEach {
        $script:dir = Join-Path ([IO.Path]::GetTempPath()) "svc-$(New-Guid)"
        New-Item -ItemType Directory -Path $script:dir | Out-Null
        $script:cfg = Join-Path $script:dir 'appsettings.Production.json'
        # Simulate an existing (upgrade) Service-mode config missing a key.
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true } }' | Set-Content $script:cfg -Encoding UTF8
    }
    AfterEach { Remove-Item $script:dir -Recurse -Force -EA SilentlyContinue }

    It 'a Service-mode unattended upgrade resolves to Merge and adds defaults' {
        # Service/Console have $siteExists=$false; presence of live config => upgrade.
        $mode = Resolve-ConfigSyncMode -Requested '' -Force $false -IsUpgrade (Test-Path $script:cfg) -Interactive $false
        $mode | Should -Be 'Merge'
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode $mode
        $after = Get-Content $script:cfg -Raw | ConvertFrom-Json
        $after.PasswordChangeOptions.PortalLockoutThreshold | Should -Be 3
        $after.PasswordChangeOptions.UseAutomaticContext     | Should -Be $true  # preserved
    }
}
```

- [ ] **Step 2 — Run, expect PASS** (depends on 24.4 + 24.2; this is the end-to-end behavioral assertion for the de-gated Service path):
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*Service/Console upgrade path*'"`
  If FAIL, the de-gating (24.5) or resolution (24.4) is wrong — STOP and re-diagnose.

- [ ] **Step 3 — Implementation:** none (asserts prior tasks).

- [ ] **Step 4 — Re-run:** GREEN.

- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.Tests.ps1 && git commit -m "test(installer): verify Service/Console upgrade syncs config with preservation [#24]"`

---

### Task 24.7 — Verify #24 acceptance criteria and close issue

**Files:** none.

- [ ] **Step 1 — Full suite green:**
  `pwsh -NoProfile -Command "$r = Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -PassThru -Output Detailed; if ($r.FailedCount -ne 0){exit 1}"`

- [ ] **Step 2 — Acceptance-criteria checklist:**
  - [x] *Existing production values never overwritten* → 24.1 guard (`PortalLockoutThreshold=99` preserved); idempotence at line 253 unchanged.
  - [x] *Missing keys added automatically with defaults* → 24.1 + 24.6 (`PortalLockoutThreshold=3` added).
  - [x] *Obsolete/unknown keys removed or explicitly marked* → Review removes (24.2 removal counter); Merge reports; drift check reports obsolete + unknown (de-gated 24.5).
  - [x] *Backup of original config created before modification* → 24.3 per-file `.bak` before write.
  - [x] *Dry-run mode shows changes without writing* → 24.2 `Diff` mode (file unchanged, "would add" lines).
  - [x] *Upgrade logs clearly indicate added/removed keys* → 24.2 "Sync summary: N added, M removed" + 24.3 durable `.log`.
  - [x] *All hosting modes covered* → 24.4 resolution + 24.5 de-gate + 24.6 behavioral.

- [ ] **Step 3 — Close:**
  `gh issue close 24 --reason completed --comment "STAB-010 closed: config sync now (1) never overwrites existing values (idempotence guard + regression test), (2) adds missing keys from schema defaults, (3) reports/removes obsolete + unknown keys, (4) creates a timestamped per-file .bak before writing, (5) supports a Diff dry-run mode that writes nothing, (6) emits an 'N added, M removed' summary plus a durable _sync-*.log, and (7) runs on IIS, Service, AND Console (de-gated from the IIS-only block; mode resolved via Resolve-ConfigSyncMode). Covered by new Pester tests in deploy/Install-PassReset.Tests.ps1."`

---

## Issue #26 (STAB-011) — Dry-run output, documented `-ConfigSync`, backups, safe default, clear logs

> **Depends on #27.** Most behavior (Diff mode, per-file backup, durable log) was implemented in #24's tasks 24.2/24.3 — they jointly satisfy #26's overlapping criteria. The remaining #26-specific work is **documentation** (help text) and a **human-readable diff** assertion + help-discoverability tests.

### Task 26.1 — Human-readable dry-run diff output (assert quality)

**Files:**
- `deploy/Install-PassReset.Tests.ps1` (append)
- `deploy/Install-PassReset.ps1` (Diff branch from 24.2 — refine output only if test reveals a gap)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Diff mode produces human-readable output (STAB-011)' {
    BeforeAll { $script:RealSchema = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PassReset.Web/appsettings.schema.json' }
    BeforeEach {
        $script:cfg = Join-Path ([IO.Path]::GetTempPath()) "hr-$(New-Guid).json"
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true } }' | Set-Content $script:cfg -Encoding UTF8
    }
    AfterEach { Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'hr-*' -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue }

    It 'output lists each addition with its default value and a no-write notice' {
        $out = Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Diff' 6>&1 | Out-String
        $out | Should -Match 'would add .*PortalLockoutThreshold = 3'
        $out | Should -Match 'No file written'
    }
}
```

- [ ] **Step 2 — Run, expect PASS** (24.2 already emits these strings). If it FAILS (e.g., default not rendered), refine the Diff "would add" line in Step 3.
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*human-readable output*'"`

- [ ] **Step 3 — Implementation:** only if Step 2 FAILED — ensure the Diff branch line in `Sync-AppSettingsAgainstSchema` reads exactly:
  ```powershell
                Write-Ok "  would add $($entry.Path) = $($entry.Default)"
  ```
  (matches the test). No change if already green.

- [ ] **Step 4 — Re-run:** GREEN.

- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.Tests.ps1 deploy/Install-PassReset.ps1 && git commit -m "test(installer): assert Diff mode emits human-readable per-key output [#26]"`

---

### Task 26.2 — Document `-ConfigSync` parameter + config-sync step in help

**Files:**
- `deploy/Install-PassReset.ps1` lines 6-13 (`.DESCRIPTION`), lines 67-71 (insert `.PARAMETER ConfigSync` after `.PARAMETER Force`), and add a `.EXAMPLE`
- `deploy/Install-PassReset.Tests.ps1` (append help-discoverability test)

- [ ] **Step 1 — Write failing test.** Append:

```powershell
Describe 'Help: -ConfigSync is documented (STAB-011)' {
    BeforeAll {
        $script:help = Get-Help (Join-Path $PSScriptRoot 'Install-PassReset.ps1') -Full
    }
    It 'documents the ConfigSync parameter' {
        $p = $script:help.parameters.parameter | Where-Object { $_.name -eq 'ConfigSync' }
        $p | Should -Not -BeNullOrEmpty
        $text = ($p.description.Text -join ' ')
        foreach ($mode in 'Merge','Review','None','Diff') { $text | Should -Match $mode }
    }
    It 'DESCRIPTION mentions the config sync step' {
        ($script:help.description.Text -join ' ') | Should -Match '(?i)config'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*-ConfigSync is documented*'"`
  Expect FAIL: no `.PARAMETER ConfigSync` block exists, and `Diff` is not in help.

- [ ] **Step 3 — Minimal implementation.** Edit `deploy/Install-PassReset.ps1`:

  (a) Extend `.DESCRIPTION` (currently ends at line 13 `6. Optionally binds an existing HTTPS certificate.`). Change:
  ```
        6. Optionally binds an existing HTTPS certificate.
  ```
  to:
  ```
        6. Optionally binds an existing HTTPS certificate.
        7. On upgrade: syncs appsettings.Production.json against the bundled schema —
           adds missing keys from defaults, reports/removes obsolete keys, backs up
           the file first, and writes a durable sync log. Runs on IIS, Service, and
           Console hosting modes. See -ConfigSync.
  ```

  (b) Insert a `.PARAMETER ConfigSync` block immediately after the `.PARAMETER Force` block (after line 70 `Use this for unattended / CI deployments.`):
  ```
  .PARAMETER ConfigSync
      Controls how appsettings.Production.json is reconciled with the schema on upgrade.
      Modes:
        Merge  - Add missing keys from schema defaults; report obsolete keys (never removed).
                 Existing values are NEVER modified.
        Review - Interactively prompt to add each missing key and to remove each obsolete key.
        Diff   - Dry-run: print every key that WOULD be added and which obsolete keys are
                 present, then exit WITHOUT writing the file or creating a backup.
        None   - Skip sync entirely.

      Default (when omitted): resolved at runtime —
        * Fresh install  -> None (template copied verbatim).
        * Upgrade + -Force or non-interactive (Service/Console/CI) -> Merge (safe, additive).
        * Interactive upgrade -> prompts the operator to choose Merge/Review/Diff/Skip.

      Merge is safe: it only adds keys and never changes existing values. Before any write,
      a timestamped backup (<config>_<yyyyMMdd-HHmmss>.bak) and a sync log
      (<config>_sync-<timestamp>.log) are created next to appsettings.Production.json.
  ```

  (c) Add an `.EXAMPLE` after the existing examples (after line 84, before the closing `#>` comment block):
  ```
  .EXAMPLE
      # Preview config changes on upgrade without writing anything:
      .\Install-PassReset.ps1 -ConfigSync Diff -Force

  .EXAMPLE
      # Unattended upgrade that adds any new keys from schema defaults:
      .\Install-PassReset.ps1 -Force -ConfigSync Merge
  ```

  (d) Update the `ValidateSet` on the `$ConfigSync` *param* (line 102) to include `Diff` so explicit `-ConfigSync Diff` binds:
  ```powershell
      [ValidateSet('Merge','Review','None','Diff')]
      [string] $ConfigSync = '',
  ```

> **Regression note (low risk):** Pure comment-based-help + a widened `ValidateSet` on the script param. The empty-string default still bypasses `ValidateSet` (PowerShell allows the default even when not in the set), and `Resolve-ConfigSyncMode` handles the empty case. Confirm `-ConfigSync Diff` now binds (was previously rejected by the param-level ValidateSet).

- [ ] **Step 4 — Run, expect PASS:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed -FullNameFilter '*-ConfigSync is documented*'"`
  Confirm the param binds: `pwsh -NoProfile -Command "$env:PASSRESET_TEST_MODE='1'; . deploy/Install-PassReset.ps1 -ConfigSync Diff; 'bind-ok'"` → `bind-ok`.

- [ ] **Step 5 — Commit:**
  `git add deploy/Install-PassReset.ps1 deploy/Install-PassReset.Tests.ps1 && git commit -m "docs(installer): document -ConfigSync parameter, modes, and safe defaults [#26]"`

---

### Task 26.3 — Update operator docs for config sync (Diff/backup/log)

**Files:**
- `docs/appsettings-Production.md` (config reference — add a "Config sync on upgrade" subsection) — read first to find the right anchor

- [ ] **Step 1 — Write failing test (doc-presence check via Pester).** Append to `deploy/schema-coverage.tests.ps1` (it already runs in CI):

```powershell
Describe 'Operator docs mention config sync modes' {
    It 'docs/appsettings-Production.md documents Diff/Merge/Review and backups' {
        $doc = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs/appsettings-Production.md'
        Test-Path $doc | Should -BeTrue
        $text = Get-Content $doc -Raw
        $text | Should -Match '(?i)-ConfigSync'
        $text | Should -Match '(?i)Diff'
        $text | Should -Match '(?i)backup'
    }
}
```

- [ ] **Step 2 — Run, expect FAIL:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -Output Detailed -FullNameFilter '*Operator docs mention config sync*'"`
  Expect FAIL (doc lacks the section).

- [ ] **Step 3 — Minimal implementation.** Read `docs/appsettings-Production.md`, then append a section (use the Technical Writer style — concise):

```markdown
## Config sync on upgrade (`-ConfigSync`)

On upgrade, `Install-PassReset.ps1` reconciles your live `appsettings.Production.json`
against the bundled `appsettings.schema.json`:

| Mode    | Behavior |
|---------|----------|
| `Merge` | Adds missing keys from schema defaults; reports obsolete keys (never removed). Existing values are never changed. |
| `Review`| Prompts before adding each missing key and before removing each obsolete key. |
| `Diff`  | Dry-run — prints what *would* change, writes nothing, creates no backup. |
| `None`  | Skips sync. |

**Default:** fresh install → `None`; unattended/`-Force` upgrade → `Merge`; interactive upgrade → prompt.

Before any write, the installer creates `appsettings.Production.json_<timestamp>.bak`
and a durable `appsettings.Production_sync-<timestamp>.log` next to your config.
Preview first with `-ConfigSync Diff`.
```

- [ ] **Step 4 — Run, expect PASS:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -Output Detailed -FullNameFilter '*Operator docs mention config sync*'"`

- [ ] **Step 5 — Commit:**
  `git add docs/appsettings-Production.md deploy/schema-coverage.tests.ps1 && git commit -m "docs: document config sync modes, backups, and dry-run for operators [#26]"`

---

### Task 26.4 — Wire Pester suite into CI (durable run for #24/#26 behavior)

**Files:**
- `.github/workflows/ci.yml` (add a step after the schema-drift gate from 27.4, before "Build solution")

- [ ] **Step 1 — Write failing check.** Confirm the installer Pester suite is not yet run in CI:
  `pwsh -NoProfile -Command "if ((Get-Content .github/workflows/ci.yml -Raw) -match 'Install-PassReset.Tests.ps1') { Write-Error 'present' } else { Write-Host 'absent' }"`
  Expect `absent`.

- [ ] **Step 2 — Run, expect FAIL** (the absence is the red state to fix).

- [ ] **Step 3 — Minimal implementation.** In `.github/workflows/ci.yml`, after the "Schema-drift gate" step (added in 27.4), insert:

```yaml

      - name: Installer Pester tests (config sync — STAB-010/011)
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          if (-not (Get-Module -ListAvailable Pester | Where-Object { $_.Version -ge [version]'5.0.0' })) {
            Install-Module Pester -MinimumVersion 5.5.0 -Force -Scope CurrentUser -SkipPublisherCheck
          }
          $r = Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -PassThru -Output Detailed
          if ($r.FailedCount -gt 0) {
            Write-Host "::error::Installer Pester suite failed ($($r.FailedCount) test(s))."
            exit 1
          }
          Write-Host "Installer Pester suite passed ($($r.PassedCount) tests)."
```

> **Regression note (low risk):** CI-only addition on `windows-latest`. The installer dot-sources behind the `PASSRESET_TEST_MODE` guard (line 748), so no install actions run. If Pester install is unavailable the step fails loudly.

- [ ] **Step 4 — Run, expect PASS** (local sanity that the suite the CI step runs is green):
  `pwsh -NoProfile -Command "$r = Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -PassThru; if ($r.FailedCount){exit 1}; 'ci-suite-green'"`
  Confirm the YAML now references the suite:
  `pwsh -NoProfile -Command "(Select-String -Path .github/workflows/ci.yml -Pattern 'Install-PassReset.Tests.ps1' -Quiet)"` → `True`.

- [ ] **Step 5 — Commit:**
  `git add .github/workflows/ci.yml && git commit -m "ci: run installer Pester suite (config sync) on every PR [#26]"`

---

### Task 26.5 — Verify #26 acceptance criteria and close issue

**Files:** none.

- [ ] **Step 1 — Full suites green:**
  `pwsh -NoProfile -Command "$a=Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -PassThru; $b=Invoke-Pester -Path deploy/schema-coverage.tests.ps1 -PassThru; if (($a.FailedCount + $b.FailedCount) -ne 0){exit 1}; 'all-green'"`

- [ ] **Step 2 — Acceptance-criteria checklist:**
  - [x] *Dry-run mode produces human-readable output* → `Diff` mode (24.2) + readability assertion (26.1): per-key "would add … = default" + "No file written".
  - [x] *`-ConfigSync` explicitly controlled and clearly documented* → `.PARAMETER ConfigSync` + widened param ValidateSet (26.2); discoverable via `Get-Help`.
  - [x] *Backups created automatically before modification* → per-file `.bak` (24.3).
  - [x] *Default behavior safe and clearly documented* → `.DESCRIPTION` step 7 + default-resolution table in help (26.2) + operator docs (26.3); Merge never overwrites (24.1).
  - [x] *Changes logged clearly* → durable `_sync-*.log` (24.3) + on-screen summary (24.2) + operator-doc pointer (26.3).

- [ ] **Step 3 — Close:**
  `gh issue close 26 --reason completed --comment "STAB-011 closed: -ConfigSync now supports a Diff dry-run mode (human-readable 'would add KEY = DEFAULT' output, writes nothing), is fully documented in comment-based help (.PARAMETER ConfigSync + .DESCRIPTION step 7 + examples) and in docs/appsettings-Production.md, creates a timestamped per-file backup before every write, defaults safely (fresh=None, unattended upgrade=Merge, interactive=prompt; Merge never overwrites), and writes a durable _sync-*.log per run. CI now runs the installer Pester suite on every PR."`

---

## Final cross-issue verification

- [ ] **Run every suite once more, from repo root:**
  `pwsh -NoProfile -Command "Invoke-Pester -Path deploy/schema-coverage.tests.ps1, deploy/Install-PassReset.Tests.ps1 -Output Detailed"`
- [ ] **Confirm installer still dot-sources and binds new param:**
  `pwsh -NoProfile -Command "$env:PASSRESET_TEST_MODE='1'; . deploy/Install-PassReset.ps1 -ConfigSync Diff; 'ok'"`
- [ ] **Confirm schema parses + full template validates:**
  `pwsh -NoProfile -Command "Test-Json -Path src/PassReset.Web/appsettings.Production.template.json -SchemaFile src/PassReset.Web/appsettings.schema.json"`
- [ ] **Confirm all three issues are closed:** `gh issue view 27 --json state -q .state; gh issue view 24 --json state -q .state; gh issue view 26 --json state -q .state` → all `CLOSED`.

---

## Risk register (medium/high changes + guards)

| Task | Risk | What could break | Guard |
|------|------|------------------|-------|
| 27.2 / 27.3 | Low-Med | `Get-SchemaKeyManifest` now emits new leaves → sync could try to add `null`-default keys | Defaults are `null`/scalars with `HasDefault`; `Set-LiveValueAtPath` idempotence (line 253) protects existing values. 24.1 guard. |
| 24.2 | Med | New `Diff` branch / ValidateSet could alter Merge/Review/None | Early `return` only for Diff; 24.1 + 24.6 guards. |
| 24.3 | Med | Backup/log file I/O on the live config path | Backup taken only when `$modified` and not dry-run; append-only log. 24.3 tests assert `.bak`/`.log` presence and Diff produces neither. |
| 24.4 | Med | Mode resolution change could block on `Read-Host` in CI or mis-resolve fresh installs | `[Console]::IsInputRedirected` keeps unattended non-interactive; explicit unit tests for all four branches. |
| 24.5 | **High** | Moving sync/drift out of IIS gate could change IIS execution timing or rely on IIS-branch locals | New block re-derives `$prodConfig`/`$schemaFile` from `$PhysicalPath`; gated on `Test-Path`; full Pester suite + dot-source parse check required before commit. |
| 26.2 | Low | Widened param ValidateSet | Empty default still valid; bind check for `-ConfigSync Diff`. |

---

**Summary of returned file paths (all absolute):**
- Plan target: `c:\Users\Phibu\Claude-Projekte\AD-Passreset-Portal\docs\superpowers\plans\2026-06-01-config-schema-and-sync.md`
- Modified: `c:\Users\Phibu\Claude-Projekte\AD-Passreset-Portal\src\PassReset.Web\appsettings.schema.json`, `...\deploy\Install-PassReset.ps1`, `...\deploy\Install-PassReset.Tests.ps1`, `...\.github\workflows\ci.yml`, `...\docs\appsettings-Production.md`
- Created: `c:\Users\Phibu\Claude-Projekte\AD-Passreset-Portal\deploy\schema-coverage.tests.ps1`
