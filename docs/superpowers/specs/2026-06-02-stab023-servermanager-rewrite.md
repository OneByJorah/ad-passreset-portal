# Spec: Replace IIS PowerShell modules with ServerManager .NET API (STAB-023, fixes 2.0.3 regression)

## Problem
The installer drives IIS via the `IISAdministration` config-API cmdlets (`Get-IISConfigSection` →
`Get-IISConfigCollection` → `Set-IISConfigAttributeValue`, `Start/Stop-IISCommitDelay`) plus
`WebAdministration` cmdlets (`New-WebAppPool`, `New-Website`, `New-IISSiteBinding`, etc.).

On hosts where these modules load via the **WinPSCompat** implicit-remoting session (PowerShell 7
+ legacy module manifests), the config objects come back **deserialized** (`Deserialized.*`) and
the config API fails (`Get-IISConfigCollection: Cannot bind parameter 'ConfigElement'`). The 2.0.3
attempt to force native load via `-SkipEditionCheck` made it WORSE: `WebAdministration` is a
**PSSnapIn-based module** and `-SkipEditionCheck` makes PS7 try to load it in-process under CoreCLR,
which throws `Could not load type 'System.Management.Automation.PSSnapIn'` → `Initialize-IIS` fails →
"required PowerShell modules could not be loaded."

## Fix (chosen architecture — Option A)
Drive IIS through the **`Microsoft.Web.Administration.ServerManager` .NET API** loaded via
`Add-Type -Path "$env:windir\system32\inetsrv\Microsoft.Web.Administration.dll"`. This is a plain
.NET assembly that loads **in-process under CoreCLR** — no module, no cmdlets, no WinPSCompat, no
deserialization. All objects are live. This works on EVERY host regardless of module edition.

Remove BOTH `Import-Module IISAdministration` and `Import-Module WebAdministration`. Remove the
`-SkipEditionCheck` calls and the `Test-IsDeserializedObject` guard (no longer relevant — there is
no module to deserialize). Keep `Test-IsDeserializedObject` the function defined (harmless, still
unit-tested) but it's no longer used by Initialize-IIS.

## Assembly load
```powershell
$mwaPath = Join-Path $env:windir 'system32\inetsrv\Microsoft.Web.Administration.dll'
Add-Type -Path $mwaPath   # throws if IIS not installed → that's the IISAvailable=$false signal
$sm = [Microsoft.Web.Administration.ServerManager]::new()
```
The DLL is present whenever IIS is installed (it's part of the IIS management stack). If
`Add-Type` throws or the file is missing, `$script:IISAvailable=$false` with a clear message
("Microsoft.Web.Administration.dll not found — is the IIS management console / role installed?").

## ServerManager object model (live, no deserialization)
- `$sm.ApplicationPools` — collection; `$sm.ApplicationPools["name"]` returns the pool or `$null`.
- `$sm.ApplicationPools.Add("name")` — create, returns the pool.
- Pool props (live): `.ManagedRuntimeVersion=''`, `.Enable32BitAppOnWin64=$false`,
  `.StartMode=[Microsoft.Web.Administration.StartMode]::AlwaysRunning`, `.AutoStart=$true`.
- `.ProcessModel.IdentityType` is enum `[Microsoft.Web.Administration.ProcessModelIdentityType]`
  (`ApplicationPoolIdentity`/`SpecificUser`/`LocalSystem`/`NetworkService`/`LocalService`).
  `.ProcessModel.UserName`, `.ProcessModel.Password` (write-only in practice — set, never read back).
- `$sm.Sites["name"]` / `$sm.Sites.Add(name, protocol, bindingInformation, physicalPath)` (HTTP) —
  but prefer creating then configuring explicitly for clarity.
- `$site.Applications["/"].ApplicationPoolName = "pool"`.
- `$site.Applications["/"].VirtualDirectories["/"].PhysicalPath = "path"`.
- `$site.Bindings` — collection. `.Add("*:80:", "http")`. For HTTPS:
  `$site.Bindings.Add("*:443:", $certHashBytes, "My")` where `$certHashBytes` is the cert
  `GetCertHash()` byte[] (NOT the thumbprint string) OR use the 3-arg overload that takes the
  thumbprint as the store. SAFER: `$b = $site.Bindings.Add("*:443:", ([byte[]]$cert.GetCertHash()), "My"); $b.Protocol='https'`.
  Iterate: `$site.Bindings | % { $_.Protocol; $_.BindingInformation }` — live props, PascalCase.
  Remove: `$site.Bindings.Remove($binding)`.
- Pool env vars: `$pool.GetCollection('environmentVariables')`; check existing via
  `$ev.GetAttributeValue('name')`; add via `$c = $coll.CreateElement('add'); $c['name']=$n; $c['value']=$v; $coll.Add($c)`.
- **Commit model:** mutate objects, then `$sm.CommitChanges()` ONCE. Replaces Start/Stop-IISCommitDelay.
  A fresh `[ServerManager]::new()` reads current config; after CommitChanges the handle is stale —
  get a fresh ServerManager for subsequent independent operations, OR do all mutations on one
  handle then commit once. Pattern: helper functions each take/return via a fresh `$sm`, mutate,
  `CommitChanges()`. State queries (exists/state) can use a fresh short-lived `$sm` each call.
- State: `$pool.State` / `$site.State` are enum `[Microsoft.Web.Administration.ObjectState]`
  (`Started`/`Stopped`/...). `.Start()` / `.Stop()` are methods on pool/site; they act immediately
  (no CommitChanges needed for start/stop). Wrap in try/catch (they throw if already in state).

## Behaviors that MUST be preserved (from the current code)
- **App-pool identity 4-branch logic** (BUG-003): explicit `-AppPoolIdentity` override (requires
  `-AppPoolPassword`); preserve existing `SpecificUser` on upgrade (don't touch identity); preserve
  other existing built-in identity on upgrade; fresh install → `ApplicationPoolIdentity`. NEVER read
  back `.Password`.
- Pool attrs: managedRuntimeVersion='', enable32Bit=$false, startMode=AlwaysRunning, autoStart=$true.
- Site: create on `$selectedHttpPort` if absent; on upgrade set root app pool + root vdir physicalPath.
- HTTPS binding: if cert found, remove any existing https binding on `$HttpsPort`, add new one bound
  to the cert (store 'My'). If `-HttpPort 0` (and cert), remove http bindings (HTTPS-only). Else keep
  an http binding on `$selectedHttpPort` for redirect (STAB-001: resolved port, not original).
- Env var idempotence: never overwrite an existing same-named var (secrets).
- Port-80 conflict detection reads existing site bindings (enumerate all sites' bindings).
- Foreign-site stop/restart tracking ($script:StoppedForeignSites) and Service-mode teardown
  (stop+remove the PassReset site and pool).
- Secret handling: SecureString → BSTR → plain → Zero/Free; null the plain var after use.
- `$script:IISAvailable` / `$script:IISLoadError` contract unchanged for callers.

## Testability
`PASSRESET_TEST_MODE` return is at line 1038 — pure helpers must be defined ABOVE it. The ServerManager
calls themselves need real IIS (can't unit-test in CI), so keep the IIS mutation in functions that the
test-mode short-circuit skips, but extract any PURE logic (e.g. binding-info string construction,
identity-branch decision) into testable helpers where reasonable. Existing Pester suite must stay green.

## Out of scope
Don't touch the config-sync, health, dependency, or banner logic. This is strictly the IIS
provisioning layer (Initialize-IIS + app-pool + site + bindings + env-vars + teardown).
