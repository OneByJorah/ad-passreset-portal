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
