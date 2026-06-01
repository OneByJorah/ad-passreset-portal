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
