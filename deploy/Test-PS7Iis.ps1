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

Heading 'ServerManager .NET API (what the installer uses — STAB-023)'
# STAB-023: the installer no longer imports IISAdministration/WebAdministration.
# It loads Microsoft.Web.Administration.dll in-process via Add-Type and drives IIS
# through the ServerManager .NET API — no module, no WinPSCompat, no deserialization.
# These probes mirror exactly what Install-PassReset.ps1 does, so OK here == install works.
$script:mwaPath = Join-Path $env:windir 'system32\inetsrv\Microsoft.Web.Administration.dll'
Probe 'Microsoft.Web.Administration.dll present' {
    if (Test-Path $script:mwaPath) { $script:mwaPath } else { $false }
}
Probe 'Add-Type loads the assembly in-process (CoreCLR)' {
    Add-Type -Path $script:mwaPath -ErrorAction Stop
    'loaded'
}
Probe 'New ServerManager + read application pools (LIVE objects)' {
    $sm = [Microsoft.Web.Administration.ServerManager]::new()
    try {
        $pools = @($sm.ApplicationPools | ForEach-Object { $_.Name })
        "$($pools.Count) pool(s): $($pools -join ', ')"
    } finally { $sm.Dispose() }
}
Probe 'ServerManager reads sites + bindings (LIVE — no Deserialized.*)' {
    $sm = [Microsoft.Web.Administration.ServerManager]::new()
    try {
        $sites = @($sm.Sites)
        $typeName = if ($sites.Count -gt 0) { @($sites[0].PSObject.TypeNames)[0] } else { '(no sites)' }
        if ($typeName -like 'Deserialized.*') {
            throw "DESERIALIZED ($typeName) — unexpected for a .NET object; report this."
        }
        $b = @($sm.Sites | ForEach-Object { $_.Bindings } | ForEach-Object { $_.BindingInformation })
        "sites=$($sites.Count), bindings=$($b.Count), site type: $typeName"
    } finally { $sm.Dispose() }
}

Heading 'Legacy module check (informational only — installer no longer uses these)'
Probe 'WebAdministration loads via WinPSCompat (expected; informational)' {
    Remove-Module WebAdministration -ErrorAction SilentlyContinue
    $warnings = $null
    Import-Module WebAdministration -WarningVariable warnings -WarningAction SilentlyContinue -ErrorAction Stop
    if ($warnings) { "compat warnings (harmless now): $($warnings -join '; ')" } else { 'imported cleanly' }
}
Probe 'WinPSCompat session present (informational)' {
    $s = Get-PSSession -Name 'WinPSCompatSession' -ErrorAction SilentlyContinue
    if ($s) { "$($s.State) on $($s.ComputerName)" } else { $false }
}

Heading 'Summary'
Write-Host ""
Write-Host "Paste the full output above into the GitHub issue for PassReset."
Write-Host "Expected (STAB-023): the 'ServerManager .NET API' probes are all OK —"
Write-Host "the dll loads via Add-Type and ServerManager reads pools/sites/bindings as LIVE objects."
Write-Host "If those are OK, the v2.0.4 installer will work regardless of how the legacy"
Write-Host "IIS modules load. The 'Legacy module check' section is informational only —"
Write-Host "WebAdministration loading via WinPSCompat is fine; the installer no longer uses it."
Write-Host ""
