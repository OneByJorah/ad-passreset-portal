#Requires -Version 7.0
<#
.SYNOPSIS
  Diagnostic probe for PS 7 + IIS module compatibility on the install target host.

.DESCRIPTION
  Paste the output into the PassReset installer issue to guide the Phase 10.5+
  migration off Set-ItemProperty "IIS:\..." to modern IISAdministration cmdlets.
  Read-only — no IIS state is modified.

.NOTES
  Run as Administrator (same context as Install-PassReset.ps1) inside PS 7:
    pwsh -NoProfile -File .\Test-PS7Iis.ps1
#>

$ErrorActionPreference = 'Continue'

function Heading([string]$Text) {
    Write-Host ""
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

function Probe([string]$Label, [scriptblock]$Block) {
    Write-Host "  $Label ... " -NoNewline
    try {
        $result = & $Block
        if ($null -eq $result -or ($result -is [bool] -and -not $result)) {
            Write-Host "FAIL" -ForegroundColor Red
        } else {
            Write-Host "OK" -ForegroundColor Green
            if ($result -isnot [bool]) { Write-Host "    -> $result" -ForegroundColor DarkGray }
        }
    } catch {
        Write-Host "ERROR" -ForegroundColor Red
        Write-Host "    -> $($_.Exception.Message)" -ForegroundColor DarkGray
    }
}

Heading 'Runtime'
Write-Host "  PSVersion:     $($PSVersionTable.PSVersion)"
Write-Host "  PSEdition:     $($PSVersionTable.PSEdition)"
Write-Host "  OS:            $([System.Environment]::OSVersion.VersionString)"
Write-Host "  OS Build:      $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue).DisplayVersion)"
Write-Host "  IsAdmin:       $((New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

Heading 'WebAdministration (legacy)'
Probe 'Module available' {
    [bool](Get-Module WebAdministration -ListAvailable)
}
Probe 'Import (native PS 7)' {
    Remove-Module WebAdministration -ErrorAction SilentlyContinue
    $warnings = $null
    Import-Module WebAdministration -WarningVariable warnings -WarningAction SilentlyContinue -ErrorAction Stop
    if ($warnings) { "imported with warnings: $($warnings -join '; ')" } else { 'imported cleanly' }
}
Probe 'IIS:\ PSDrive visible' {
    [bool](Get-PSDrive -Name 'IIS' -ErrorAction SilentlyContinue)
}
Probe 'WebAdministration PSProvider visible' {
    [bool](Get-PSProvider -PSProvider WebAdministration -ErrorAction SilentlyContinue)
}
Probe 'Test-Path IIS:\AppPools' {
    Test-Path 'IIS:\AppPools'
}
Probe 'Get-Item IIS:\AppPools\DefaultAppPool' {
    $p = Get-Item 'IIS:\AppPools\DefaultAppPool' -ErrorAction Stop
    $p.Name
}

Heading 'IISAdministration (modern)'
Probe 'Module available' {
    [bool](Get-Module IISAdministration -ListAvailable)
}
Probe 'Import native (-SkipEditionCheck, as the installer does)' {
    Remove-Module IISAdministration -ErrorAction SilentlyContinue
    $warnings = $null
    Import-Module IISAdministration -SkipEditionCheck -WarningVariable warnings -WarningAction SilentlyContinue -ErrorAction Stop
    if ($warnings) { "imported with warnings: $($warnings -join '; ')" } else { 'imported cleanly' }
}
Probe 'Get-IISAppPool command available' {
    [bool](Get-Command Get-IISAppPool -ErrorAction SilentlyContinue)
}
Probe 'Get-IISAppPool returns DefaultAppPool' {
    $p = Get-IISAppPool -Name DefaultAppPool -ErrorAction Stop
    "$($p.Name) (state: $($p.State))"
}
Probe 'Get-IISSite returns something' {
    $sites = Get-IISSite -ErrorAction Stop
    "$($sites.Count) site(s): $($sites.Name -join ', ')"
}
Probe 'Get-IISServerManager callable' {
    $sm = Get-IISServerManager
    if ($sm) { $sm.GetType().FullName } else { $false }
}
# STAB-022: the decisive probe. If the config section comes back as a
# "Deserialized.*" type, IISAdministration is routing through WinPSCompat and the
# installer's Get-IISConfigCollection / $_.Protocol calls will FAIL. This must be live.
Probe 'Config section is LIVE (not Deserialized.* — STAB-022)' {
    $section = Get-IISConfigSection -SectionPath 'system.applicationHost/sites' -ErrorAction Stop
    $typeName = @($section.PSObject.TypeNames)[0]
    if ($typeName -like 'Deserialized.*') {
        throw "DESERIALIZED ($typeName) — IISAdministration loaded via WinPSCompat; install will fail. Update IISAdministration or run in a session where it loads natively."
    }
    "live: $typeName"
}

Heading 'Compatibility session check'
Probe 'WinPSCompat session present' {
    $s = Get-PSSession -Name 'WinPSCompatSession' -ErrorAction SilentlyContinue
    if ($s) { "$($s.State) on $($s.ComputerName)" } else { $false }
}

Heading 'Summary'
Write-Host ""
Write-Host "Paste the full output above into the GitHub issue for PassReset."
Write-Host "Expected outcome: IISAdministration loads natively (no compat session),"
Write-Host "the 'Config section is LIVE' probe is OK (not Deserialized.*), and Get-IISAppPool works."
Write-Host "If 'Config section is LIVE' shows DESERIALIZED, the install will fail (STAB-022) —"
Write-Host "update the IISAdministration module (Install-Module IISAdministration -Scope AllUsers)."
Write-Host ""
