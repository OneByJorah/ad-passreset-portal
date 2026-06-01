# Pester tests for Install-PassReset.ps1.
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path deploy/Install-PassReset.Tests.ps1 -Output Detailed"
#
# These tests exercise the installer in "dry-run" / function-extraction mode. We
# dot-source the installer to load its functions without executing its top-level
# install flow. The installer's top-level script block checks for the
# $PASSRESET_TEST_MODE env var and short-circuits when set.

BeforeAll {
    $env:PASSRESET_TEST_MODE = '1'
    . "$PSScriptRoot/Install-PassReset.ps1"
}

AfterAll {
    Remove-Item Env:PASSRESET_TEST_MODE -ErrorAction SilentlyContinue
}

Describe 'Install-PassReset: -HostingMode param' {
    It 'accepts IIS' {
        { Test-HostingModeValue -HostingMode 'IIS' } | Should -Not -Throw
    }
    It 'accepts Service' {
        { Test-HostingModeValue -HostingMode 'Service' } | Should -Not -Throw
    }
    It 'accepts Console' {
        { Test-HostingModeValue -HostingMode 'Console' } | Should -Not -Throw
    }
    It 'rejects unknown values' {
        { Test-HostingModeValue -HostingMode 'Nonsense' } | Should -Throw
    }
}

Describe 'Install-PassReset: Test-ServiceModePreflight' {
    It 'returns $false when cert thumbprint is empty' {
        Test-ServiceModePreflight -CertThumbprint '' -Port 443 -ServiceAccount 'NT SERVICE\PassReset' |
            Should -BeFalse
    }
    It 'returns $false when Port is already bound' {
        # Bind a TCP listener on a free high port, then assert preflight fails.
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        try {
            $port = ($listener.LocalEndpoint).Port
            Test-ServiceModePreflight -CertThumbprint 'ABCDEF' -Port $port -ServiceAccount 'NT SERVICE\PassReset' |
                Should -BeFalse
        } finally {
            $listener.Stop()
        }
    }
}

Describe 'Get-SchemaKeyManifest: handles scalar/nullable leaves under StrictMode (regression)' {
    BeforeAll {
        $script:RealSchema = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PassReset.Web/appsettings.schema.json'
    }
    It 'builds a manifest from the real schema without throwing' {
        $schema = Get-Content $script:RealSchema -Raw | ConvertFrom-Json
        { Get-SchemaKeyManifest -Schema $schema } | Should -Not -Throw
    }
    It 'emits leaf entries for nullable ["string","null"] properties (e.g. LocalPolicy.BannedWordsPath)' {
        $schema   = Get-Content $script:RealSchema -Raw | ConvertFrom-Json
        $manifest = Get-SchemaKeyManifest -Schema $schema
        $paths    = @($manifest | ForEach-Object { $_.Path })
        $paths | Should -Contain 'PasswordChangeOptions:LocalPolicy:BannedWordsPath'
        $paths | Should -Contain 'AdminSettings:LoopbackPort'
    }
}

Describe 'Get-SchemaKeyManifest: tolerates nodes without an explicit type (StrictMode)' {
    It 'does not throw on a property node that omits type (e.g. $ref/enum-only)' {
        # A schema where a property has no "type" (valid JSON Schema: enum-only).
        $schema = '{ "type":"object", "properties": { "Mode": { "enum": ["A","B"], "default": "A" } } }' | ConvertFrom-Json
        { Get-SchemaKeyManifest -Schema $schema } | Should -Not -Throw
        $m = Get-SchemaKeyManifest -Schema $schema
        @($m | ForEach-Object { $_.Path }) | Should -Contain 'Mode'
    }
}

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

Describe 'Sync-AppSettingsAgainstSchema: adds keys when an entire parent section is missing (#24)' {
    BeforeAll { $script:RealSchema = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PassReset.Web/appsettings.schema.json' }
    BeforeEach { $script:cfg = Join-Path ([IO.Path]::GetTempPath()) "mp-$(New-Guid).json" }
    AfterEach  { Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'mp-*' -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue }

    It 'adds an entire missing section (e.g. AdminSettings) from schema defaults' {
        # Live config lacks AdminSettings entirely (simulates upgrade from older release).
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true } }' | Set-Content $script:cfg -Encoding UTF8
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Merge'
        $after = Get-Content $script:cfg -Raw | ConvertFrom-Json
        $after.AdminSettings.LoopbackPort | Should -Be 5010
        $after.AdminSettings.Enabled      | Should -Be $false
    }
    It 'still adds keys whose parent already exists (no regression)' {
        '{ "PasswordChangeOptions": { "UseAutomaticContext": true } }' | Set-Content $script:cfg -Encoding UTF8
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Merge'
        $after = Get-Content $script:cfg -Raw | ConvertFrom-Json
        $after.PasswordChangeOptions.PortalLockoutThreshold | Should -Be 3
    }
}

Describe 'Remove-LiveValueAtPath: StrictMode-safe on empty/absent nodes (#24/#26)' {
    It 'returns false (no throw) when the leaf is absent under an empty parent object' {
        $live = [PSCustomObject]@{ Section = [PSCustomObject]@{} }  # empty parent
        { Remove-LiveValueAtPath -Config $live -Path 'Section:Missing' } | Should -Not -Throw
        Remove-LiveValueAtPath -Config $live -Path 'Section:Missing' | Should -Be $false
    }
    It 'removes an existing leaf and returns true' {
        $live = [PSCustomObject]@{ Section = [PSCustomObject]@{ Old = 'x' } }
        Remove-LiveValueAtPath -Config $live -Path 'Section:Old' | Should -Be $true
        $null -ne $live.Section.PSObject.Properties['Old'] | Should -Be $false
    }
}

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
        @(Get-ChildItem $script:dir -Filter 'appsettings.Production.json_*.bak').Count | Should -BeGreaterThan 0
    }
    It 'does NOT create a backup in Diff mode (no write)' {
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Diff'
        @(Get-ChildItem $script:dir -Filter '*.bak').Count | Should -Be 0
    }
    It 'writes a durable sync log listing additions' {
        Sync-AppSettingsAgainstSchema -SchemaPath $script:RealSchema -ConfigPath $script:cfg -Mode 'Merge'
        $log = Get-ChildItem $script:dir -Filter 'appsettings.Production_sync-*.log' | Select-Object -First 1
        $log | Should -Not -BeNullOrEmpty
        (Get-Content $log.FullName -Raw) | Should -Match 'PortalLockoutThreshold'
    }
}

Describe 'Resolve-ConfigSyncMode: hosting-mode aware resolution' {
    It 'returns Merge when -Force regardless of mode' {
        Resolve-ConfigSyncMode -Requested '' -Force $true -IsUpgrade $false -Interactive $false | Should -Be 'Merge'
    }
    It 'returns None on a fresh non-interactive install' {
        Resolve-ConfigSyncMode -Requested '' -Force $false -IsUpgrade $false -Interactive $false | Should -Be 'None'
    }
    It 'honors an explicit -ConfigSync value' {
        Resolve-ConfigSyncMode -Requested 'Diff' -Force $false -IsUpgrade $true -Interactive $true | Should -Be 'Diff'
    }
    It 'returns Merge on a non-interactive upgrade (Service/Console unattended)' {
        Resolve-ConfigSyncMode -Requested '' -Force $false -IsUpgrade $true -Interactive $false | Should -Be 'Merge'
    }
}

Describe 'Config sync runs for all hosting modes (de-gated from IIS block) [#24]' {
    It 'invokes Sync/Drift after the IIS hosting block closes' {
        $src   = Get-Content (Join-Path $PSScriptRoot 'Install-PassReset.ps1') -Raw
        $lines = $src -split "`r?`n"
        $iisClose = ($lines | Select-String -SimpleMatch '# ─── end IIS hosting block ───').LineNumber | Select-Object -First 1
        $syncCall = ($lines | Select-String -SimpleMatch 'Sync-AppSettingsAgainstSchema').LineNumber | Select-Object -Last 1
        $iisClose | Should -Not -BeNullOrEmpty -Because 'an end-marker comment must exist on the IIS block close'
        $syncCall | Should -BeGreaterThan $iisClose -Because 'sync must run after the IIS block closes (all modes)'
    }
}

Describe 'Service/Console upgrade path syncs config (behavioral) [#24]' {
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
    It 'accepts -ConfigSync Diff at the param binding (ValidateSet includes Diff)' {
        $env:PASSRESET_TEST_MODE = '1'
        { . (Join-Path $PSScriptRoot 'Install-PassReset.ps1') -ConfigSync Diff } | Should -Not -Throw
    }
}

Describe 'Install-PassReset: Test-HttpsBinding' {
    It 'reports OK when an HTTPS binding exists on the target port' {
        $bindings = @(
            [pscustomobject]@{ protocol = 'https'; bindingInformation = '*:443:' }
        )
        $result = Test-HttpsBinding -Bindings $bindings -HttpsPort 443
        $result.HasHttps | Should -BeTrue
    }
    It 'reports missing when no HTTPS binding exists on the target port' {
        $bindings = @(
            [pscustomobject]@{ protocol = 'http'; bindingInformation = '*:80:' }
        )
        $result = Test-HttpsBinding -Bindings $bindings -HttpsPort 443
        $result.HasHttps | Should -BeFalse
    }
    It 'reports missing when HTTPS exists only on a different port' {
        $bindings = @(
            [pscustomobject]@{ protocol = 'https'; bindingInformation = '*:8443:' }
        )
        $result = Test-HttpsBinding -Bindings $bindings -HttpsPort 443
        $result.HasHttps | Should -BeFalse
    }
}

Describe 'STAB-018 / #34 post-deploy health contract' {
    BeforeAll {
        $script:InstallerPath = Join-Path $PSScriptRoot 'Install-PassReset.ps1'
        $script:InstallerText = Get-Content -Raw -Path $script:InstallerPath
    }

    It 'treats HTTP 200 from /api/health as deployment success' {
        $script:InstallerText | Should -Match '\$lastHealth\.StatusCode -eq 200'
    }

    It 'queries the /api/health endpoint during post-deploy verification' {
        $script:InstallerText | Should -Match '/api/health'
    }

    It 'hard-fails the install when the health check never returns 200' {
        $script:InstallerText | Should -Match 'Post-deploy health check failed'
        $script:InstallerText | Should -Match 'exit 1'
    }
}

Describe 'STAB-018 HealthCheckSettings config surface' {
    BeforeAll {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $script:Template = Join-Path $repoRoot 'src/PassReset.Web/appsettings.Production.template.json'
        $script:Schema   = Join-Path $repoRoot 'src/PassReset.Web/appsettings.schema.json'
    }

    It 'template includes a HealthCheckSettings block with all four keys' {
        $json = Get-Content -Raw -Path $script:Template | ConvertFrom-Json
        $json.HealthCheckSettings | Should -Not -BeNullOrEmpty
        $json.HealthCheckSettings.DisableSmtpConnectivityProbe    | Should -BeOfType [bool]
        $json.HealthCheckSettings.DisableExpiryServiceCheck       | Should -BeOfType [bool]
        $json.HealthCheckSettings.DisableAdConnectivityProbe      | Should -BeOfType [bool]
        $json.HealthCheckSettings.ExpiryServiceGracePeriodSeconds | Should -Be 600
    }

    It 'schema declares HealthCheckSettings and remains valid JSON' {
        $schemaText = Get-Content -Raw -Path $script:Schema
        $schemaText | Should -Match 'HealthCheckSettings'
        { $schemaText | ConvertFrom-Json } | Should -Not -Throw
    }
}

Describe 'STAB-018 documentation' {
    It 'appsettings-Production.md documents HealthCheckSettings and the four keys' {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $doc = Get-Content -Raw -Path (Join-Path $repoRoot 'docs/appsettings-Production.md')
        $doc | Should -Match 'HealthCheckSettings'
        $doc | Should -Match 'DisableSmtpConnectivityProbe'
        $doc | Should -Match 'DisableExpiryServiceCheck'
        $doc | Should -Match 'DisableAdConnectivityProbe'
        $doc | Should -Match 'ExpiryServiceGracePeriodSeconds'
    }
}

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
Describe 'CI: PowerShell quality gate present' {
    BeforeAll {
        $repo = Split-Path $PSScriptRoot -Parent
        $script:Ci  = Get-Content (Join-Path $repo '.github/workflows/ci.yml') -Raw
        $script:Rel = Get-Content (Join-Path $repo '.github/workflows/release.yml') -Raw
    }
    It 'ci.yml defines a powershell-quality job' { $script:Ci | Should -Match 'powershell-quality:' }
    It 'ci.yml runs PSScriptAnalyzer'           { $script:Ci | Should -Match 'PSScriptAnalyzer' }
    It 'ci.yml runs the installer Pester suites' { $script:Ci | Should -Match 'Invoke-Pester' }
    It 'ci.yml enforces no UTF-8 BOM on deploy scripts' { $script:Ci | Should -Match '(?i)BOM' }
    It 'release.yml gates release on powershell-quality' {
        $script:Rel | Should -Match 'needs:'
        $script:Rel | Should -Match 'powershell-quality'
    }
}
Describe 'editorconfig: ps1 encoding policy' {
    It 'has a [*.ps1...] section enforcing utf-8' {
        $repo = Split-Path $PSScriptRoot -Parent
        $ec = Get-Content (Join-Path $repo '.editorconfig') -Raw
        $ec | Should -Match '\[\*\.\{ps1[^\]]*\}\]'
        $ec | Should -Match 'charset = utf-8'
    }
}

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
        # Accept both $selectedHttpPort and ${selectedHttpPort} (brace-delimited) forms.
        $script:HttpBindingText | Should -Match '\$\{?selectedHttpPort\}?'
    }

    It 'does not bind the original $HttpPort param when creating the retained HTTP binding' {
        # $HttpPort or ${HttpPort}, but NOT $selectedHttpPort (negative lookbehind on "selected").
        $script:HttpBindingText | Should -Not -Match '\$\{?HttpPort\}?'
    }
}

Describe 'Install-PassReset: STAB-001 HTTPS-only removal guard intact' {
    It 'keeps the -HttpPort 0 removal branch guarded by $HttpPort -le 0' {
        $src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw
        $src | Should -Match 'if \(\$CertThumbprint -and \$HttpPort -le 0\)'
    }
}

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

Describe 'Install-PassReset: Get-HostingBundleDiagnostic' {
    It 'reports not-detected when version is null' {
        Get-HostingBundleDiagnostic -InstalledVersion $null | Should -Match 'not detected in HKLM registry'
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

Describe 'Install-PassReset: IIS prereq block wiring' {
    BeforeAll { $script:Src = Get-Content "$PSScriptRoot/Install-PassReset.ps1" -Raw }
    It 'uses Resolve-DependencyAction for the missing-feature decision' { $script:Src | Should -Match 'Resolve-DependencyAction' }
    It 'collects DISM exit codes for reboot detection' { $script:Src | Should -Match 'Test-DismRebootPending' }
    It 're-validates IIS features after DISM' {
        ([regex]::Matches($script:Src, 'Get-WindowsFeature')).Count | Should -BeGreaterThan 1
    }
    It 'honors -SkipDependencyCheck' { $script:Src | Should -Match '\$SkipDependencyCheck' }
}