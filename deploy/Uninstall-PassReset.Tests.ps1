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
