# UAT Runbook — #34 (STAB-019) Post-Deploy Health Verification

**Status:** pending — requires a live Windows Server + IIS test host (domain-joined or with reachable AD).
**Merged in:** PR #60 (`master` @ `eefc90c`). Code: `deploy/Install-PassReset.ps1`.
**Scope:** HIGH-risk operator UAT for the four #34 acceptance criteria. The automated Pester suite (103/0) exercises the extracted evaluators + source wiring; this runbook validates a real IIS round-trip that CI cannot.

---

## What changed (so you know what you're verifying)

The post-deploy verification block now:
1. **Fails on non-healthy aggregate status** — `Test-HealthResponseHealthy` requires `/api/health` to return `{ "status": "healthy" }`, not just HTTP 200. A 200 with `status: degraded`/`unhealthy` is a failure.
2. **Emits actionable diagnostics** — `Get-HealthFailureDiagnostics` prints a multi-line block (logs path, Event Viewer source/ID 1001, app-pool state, binding/port, common causes) before `exit 1`.
3. **Resolves custom host headers** — `Resolve-HealthHostHeader` reads `$hostHeader` from the actual IIS binding (`*:port:hostname`), preferring the HTTPS binding when a cert is bound, so custom-hostname sites are probed correctly instead of always hitting `$env:COMPUTERNAME`.
4. **Runs in all hosting modes** — IIS (full retry loop), Service (Kestrel endpoint, HTTPS 443), and Console (prints a manual-verification note; app not auto-started).

`-SkipHealthCheck` bypasses verification in every mode (air-gapped hosts).

---

## Pre-requisites for the test host

- Windows Server 2019/2022/2025 with IIS 10 + the .NET 10 Hosting Bundle.
- PowerShell 7 (`pwsh`).
- A built publish folder (`dotnet publish src\PassReset.Web -c Release -o deploy\publish`) and a release zip or working tree.
- An HTTPS certificate in `Cert:\LocalMachine\My` (self-signed is fine for UAT) — note its thumbprint.
- Reachable AD (domain-joined) OR `UseDebugProvider:true` in the deployed `appsettings.Production.json` to make AD/SMTP probes green without real infra. **Recommended for UAT:** debug provider so health is `healthy` by default and you can deliberately break one probe in Test C.

> Tip: take a VM snapshot before starting so you can re-run destructive simulations (broken SMTP, BOM, etc.) cleanly.

---

## Tests

### A. IIS happy path — healthy deploy passes (criteria 1)
**Steps:** Fresh/upgrade install in IIS mode with a valid cert and a config that yields healthy `/api/health` (debug provider, or real AD reachable):
```powershell
pwsh -File .\deploy\Install-PassReset.ps1 -CertThumbprint "<THUMB>" -HttpsPort 443
```
**Expected:** `[>>] Verifying deployment at https://<host>:443 (up to 10 x 2s)` then `[OK] Health OK -- AD: <status>, SMTP: <status>, ExpiryService: <status>`. Installer completes successfully, exit 0.
**Result:** _pending_

### B. Custom host-header binding is honored (criteria 3)
**Steps:** Bind the site to a custom host header (not COMPUTERNAME), e.g. `passreset.corp.local`, with a matching `hosts` entry pointing at 127.0.0.1, then install/upgrade:
```powershell
# After install, or via -SiteName matching an existing host-header binding:
# add to C:\Windows\System32\drivers\etc\hosts:  127.0.0.1  passreset.corp.local
```
**Expected:** the `Verifying deployment at ...` line shows `https://passreset.corp.local:443` (the bound hostname), NOT `https://<COMPUTERNAME>:443`. Health check succeeds against the host header.
**Result:** _pending_

### C. Degraded/unhealthy status hard-fails with diagnostics (criteria 1 + 2)
**Steps:** Make one dependency unhealthy so `/api/health` returns `status: degraded` or `unhealthy` with HTTP 503 — e.g. point SMTP at an unreachable host while email is enabled, OR enable the expiry service with `HealthCheckSettings:ExpiryServiceGracePeriodSeconds=0` so a not-yet-run service reports `degraded`:
```powershell
# In the deployed appsettings.Production.json before the verification runs, or re-run -Reconfigure:
#   "EmailNotificationSettings": { "Enabled": true },
#   "SmtpSettings": { "Host": "192.0.2.1", "Port": 1 }
```
**Expected:** the retry loop exhausts 10 attempts, then prints the `Get-HealthFailureDiagnostics` block (must reference: the logs path `...\inetpub\logs\PassReset`, Event Viewer / source PassReset / ID 1001, app-pool state, the binding/port, common causes), then `Post-deploy health check failed after 10 attempts...` and **exits 1**. The install does NOT report success.
**Result:** _pending_

### D. `-SkipHealthCheck` bypasses verification (regression guard)
**Steps:** Run the same install as Test C (broken SMTP) but add `-SkipHealthCheck`:
```powershell
pwsh -File .\deploy\Install-PassReset.ps1 -CertThumbprint "<THUMB>" -HttpsPort 443 -SkipHealthCheck
```
**Expected:** `[>>] Skipping post-deploy health check (-SkipHealthCheck specified)`. Install completes (exit 0) despite the unhealthy dependency — verification was bypassed.
**Result:** _pending_

### E. Service mode verification (criteria 4)
**Steps:** Install in Service mode (requires a cert; the service self-hosts Kestrel on HTTPS 443):
```powershell
pwsh -File .\deploy\Install-PassReset.ps1 -HostingMode Service -CertThumbprint "<THUMB>" -ServiceAccount "NT SERVICE\PassReset"
```
**Expected:** `[>>] Verifying service at https://<COMPUTERNAME>:443/api/health (up to 10 x 2s)` then `[OK] Service healthy at https://<COMPUTERNAME>:443/api/health`. If the service doesn't answer healthy, the diagnostics block prints and the install exits 1.
**Result:** _pending_

### F. Console mode prints a manual-verification note (criteria 4)
**Steps:** Install in Console mode:
```powershell
pwsh -File .\deploy\Install-PassReset.ps1 -HostingMode Console
```
**Expected:** prints `Console mode: app is not auto-started, so no health check runs.` and `After starting it manually, verify: Invoke-WebRequest https://localhost:<HttpsPort>/api/health`. No health probe runs; install completes.
**Result:** _pending_

### G. Fresh-deploy with expiry service enabled returns healthy (cross-check #31)
**Steps:** Install with `PasswordExpiryNotificationSettings:Enabled=true` and the default grace (`HealthCheckSettings` absent → 600s). The expiry background service won't have ticked when the post-deploy check runs.
**Expected:** `/api/health` reports `expiryService: healthy` (within grace), aggregate `healthy`, install succeeds. This is the #31↔#34 contract — a fresh deploy with the expiry service enabled must NOT fail verification.
**Result:** _pending_

---

## Sign-off

| Test | Criterion | Result |
|------|-----------|--------|
| A. IIS happy path | 1 (healthy passes) | pending |
| B. Custom host header | 3 (host-header resolution) | pending |
| C. Degraded → fail + diagnostics | 1 + 2 | pending |
| D. -SkipHealthCheck bypass | regression | pending |
| E. Service mode | 4 | pending |
| F. Console mode | 4 | pending |
| G. Fresh deploy + expiry enabled | #31↔#34 | pending |

**total:** 7 · **passed:** 0 · **failed:** 0 · **pending:** 7

After running, record `pass`/`fail` per row. Any `fail` → file a follow-up issue referencing STAB-019 with the diagnostics output, and reopen #34 if a criterion regressed.
