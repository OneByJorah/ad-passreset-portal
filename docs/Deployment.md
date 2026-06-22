# PassReset — Deployment Guide

PassReset (v2.0+) supports three Windows hosting modes plus cross-platform LDAP hosting. This
guide is the entry point: pick a mode below, then follow its section or the linked detailed guide.

The installer (`deploy/Install-PassReset.ps1`) selects the mode via `-HostingMode IIS|Service|Console`.
**IIS is the default**, and upgrades to an existing IIS install stay on IIS with no flag required.

---

## Choosing a hosting mode

| Mode | Best for | TLS termination | Detailed guide |
|------|----------|-----------------|----------------|
| **IIS** (default) | Domain-joined Windows servers, existing IIS estates | IIS HTTPS binding | [IIS-Setup.md](IIS-Setup.md) |
| **Windows Service** | Windows hosts without IIS; self-contained service | Kestrel (`Kestrel:HttpsCert`) | [§ Service mode](#service-mode) below |
| **Console** | Debugging, non-IIS hosts, container-style foreground runs | Kestrel | [§ Console mode](#console-mode) below |
| **Linux / Docker** | Any `net10.0` host (LDAP provider) | reverse proxy / Kestrel | [AD-ServiceAccount-LDAP-Setup.md](AD-ServiceAccount-LDAP-Setup.md) |

Mode is captured once at startup. `HostingModeDetector` auto-detects IIS (via the
`ASPNETCORE_IIS_HTTPAUTH` environment variable) and Windows Service (via
`WindowsServiceHelpers.IsWindowsService()`); the installer's `-HostingMode` makes the choice
explicit at install time.

---

## IIS mode (default)

The canonical, fully detailed walkthrough — IIS roles, the .NET 10 Hosting Bundle, certificate
options, app-pool identity, binding, secrets, upgrade/reconfigure — lives in
**[IIS-Setup.md](IIS-Setup.md#step-1--enable-iis)**.

Quick start:

```powershell
pwsh -File .\deploy\Install-PassReset.ps1 -CertThumbprint "PASTE_THUMBPRINT_HERE"
# (-HostingMode IIS is the default and may be omitted)
```

---

## Service mode

Runs PassReset as a self-contained **Windows Service** with Kestrel terminating TLS directly —
no IIS required. Use this on Windows hosts where you don't want the IIS role.

### Install

```powershell
pwsh -File .\deploy\Install-PassReset.ps1 `
    -HostingMode    Service `
    -PhysicalPath   "C:\Program Files\PassReset" `
    -PublishFolder  ".\publish" `
    -CertThumbprint "PASTE_THUMBPRINT_HERE"
# Cert alternative (mutually exclusive with -CertThumbprint):
#   -PfxPath "C:\certs\passreset.pfx" -PfxPassword (Read-Host 'PFX password' -AsSecureString)
```

### Service identity

| Setting | Default | Notes |
|---------|---------|-------|
| `-ServiceAccount` | `NT SERVICE\PassReset` | A **virtual account** — created by the Service Control Manager with no password. Recommended. |
| `-ServicePassword` | *(none)* | Required **only** for a domain service account (e.g. `YOURDOMAIN\svc-passreset`); pass as a `SecureString`. |

The service is registered with startup type **AutomaticDelayedStart**. A domain account install
needs the same AD delegation as IIS mode — see
[AD-ServiceAccount-Setup.md](AD-ServiceAccount-Setup.md).

### TLS (Kestrel)

In Service mode the HTTPS certificate is read from the `Kestrel:HttpsCert` configuration section,
which the installer writes from `-CertThumbprint` / `-PfxPath`. Two forms are supported:

- **Store thumbprint** — `Thumbprint` + `StoreLocation` (default `LocalMachine`) + `StoreName`
  (default `My`). The validator forbids the `CurrentUser` store in Service mode (the service
  identity has no user profile).
- **PFX file** — `PfxPath` (+ optional `PfxPassword`).

Exactly one of the two must be set in Service mode; the `KestrelHttpsCertOptions` validator fails
fast at startup otherwise. Kestrel binds `0.0.0.0:443`.

### Self-signed certificate fallback

`-AllowSelfSignedCertificate` defaults to **`$true`**: if no certificate is supplied, the installer
generates a self-signed cert for the service. For production, supply a real cert via
`-CertThumbprint` or `-PfxPath`. Pass `-AllowSelfSignedCertificate:$false` to require an explicit cert.

### Preflight

Before registering the service the installer runs `Test-ServiceModePreflight`: it resolves the
HTTPS certificate, confirms the port is free (accounting for a site being migrated away from IIS),
and validates the service account. Any failure aborts before the service is created.

### Migrating an existing IIS install to Service mode

Switching an IIS install to `-HostingMode Service` is a deliberate operator choice (never an
automatic upgrade side effect). When detected, the installer tears down the IIS site and app pool
after preflight passes, then registers the service against the same port.

### Manage the service

```powershell
Get-Service PassReset
Start-Service PassReset ; Stop-Service PassReset
# Health check:
Invoke-WebRequest https://localhost/api/health -UseBasicParsing
```

---

## Console mode

Runs the host in the foreground — useful for debugging, non-IIS hosts, or container-style runs.

```powershell
pwsh -File .\deploy\Install-PassReset.ps1 -HostingMode Console -PublishFolder ".\publish"
# or run the published host directly:
.\PassReset.Web.exe
```

Console mode uses Kestrel and the same `Kestrel:HttpsCert` configuration as Service mode. Stop with
`Ctrl+C`. There is no service registration and no IIS dependency.

---

## Configuration & secrets (all modes)

- Full key reference: [appsettings-Production.md](appsettings-Production.md).
- Secret handling (env vars / encrypted store): [Secret-Management.md](Secret-Management.md).
- Optional loopback admin UI for browser-based config editing: [Admin-UI.md](Admin-UI.md).
- AD service-account delegation: [AD-ServiceAccount-Setup.md](AD-ServiceAccount-Setup.md)
  (Windows) / [AD-ServiceAccount-LDAP-Setup.md](AD-ServiceAccount-LDAP-Setup.md) (cross-platform LDAP).

---

*For the detailed IIS walkthrough, see [IIS-Setup.md](IIS-Setup.md). For non-Windows / LDAP
hosting, see [AD-ServiceAccount-LDAP-Setup.md](AD-ServiceAccount-LDAP-Setup.md).*
