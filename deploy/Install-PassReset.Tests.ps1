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
