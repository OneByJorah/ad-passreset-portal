# Audit Events

PassReset emits security and audit events through `ISiemService` so SOC operators can monitor password-change activity and failure modes. This document is the authoritative reference for the event types, the structured `AuditEvent` fields, correlation, and the syslog / email-alert configuration that controls them.

For the SIEM configuration keys themselves, see [appsettings-Production.md](appsettings-Production.md#siemsettings).

## Overview

Events are forwarded to two optional, independent channels (both opt-in via `SiemSettings`):

- **Syslog** — RFC 5424 over UDP or TCP (`SiemSettings.Syslog`).
- **Email alerts** — a filtered subset delivered by email (`SiemSettings.AlertEmail`).

`ISiemService` exposes two emission shapes:

| Overload | Shape | Used by |
|----------|-------|---------|
| `LogEvent(SiemEventType eventType, string username, string ipAddress, string? detail)` | Legacy positional form | `RateLimitExceeded` (rate-limiter rejection in `Program.cs`) and `Generic` (HIBP range-fetch unavailable in `PasswordController.PwnedCheckAsync`) |
| `LogEvent(AuditEvent evt)` | Structured DTO (STAB-015) | The password-change path in `PasswordController` — every `Audit(...)` call that carries a `SiemEventType` |

The structured form (`LogEvent(AuditEvent)`) carries a `TraceId` for cross-log correlation and emits an RFC 5424 STRUCTURED-DATA element. The legacy overload remains for the two non-password-change call sites listed above.

## Event types

Every `SiemEventType` value, the syslog severity assigned in `SiemService.SeverityMap`, and when it fires. Severity numbers follow RFC 5424 (lower = more severe).

| Event type | Severity | When it fires |
|------------|----------|---------------|
| `PasswordChanged` | 5 (Notice) | A password change completed successfully (`Audit("Success", …)`). |
| `InvalidCredentials` | 4 (Warning) | The supplied current password was wrong (mapped from `ApiErrorCode.InvalidCredentials`). |
| `UserNotFound` | 5 (Notice) | The username was not found in AD (mapped from `ApiErrorCode.UserNotFound`). |
| `PortalLockout` | 4 (Warning) | Portal lockout threshold reached — further attempts blocked without contacting AD. |
| `ApproachingLockout` | 4 (Warning) | One more wrong attempt will trigger portal lockout. |
| `RateLimitExceeded` | 4 (Warning) | Request rejected by the per-IP rate limiter (429). Emitted via the legacy overload in `Program.cs`. |
| `RecaptchaFailed` | 4 (Warning) | reCAPTCHA v3 validation failed (`Audit("RecaptchaFailed", …)`). |
| `ChangeNotPermitted` | 4 (Warning) | Password change not permitted by group-membership rules (mapped from `ApiErrorCode.ChangeNotPermitted`). |
| `ValidationFailed` | 5 (Notice) | Request rejected by model validation (`Audit("ValidationFailed", …)`). |
| `Generic` | 3 (Error) | Unexpected failure: an unmapped `ApiErrorCode` on the password path, or a HIBP range-fetch outage in `PwnedCheckAsync` (legacy overload). |
| `PasswordChangeAttemptStarted` | 6 (Informational) | A password-change attempt entered the controller — the correlation anchor (`Audit("AttemptStarted", …)`). |

> **Note:** the minimum-distance rejection logs `Audit("DistanceTooLow", …)` with **no** `SiemEventType`, so it is written to the application log only and produces **no** SIEM/syslog event. There is no `MinimumDistance` member in `SiemEventType`.

## AuditEvent fields

`AuditEvent` (`src/PassReset.Web/Services/AuditEvent.cs`) is a sealed record. It is an allowlist DTO: only the fields below exist.

| Field | Type | Meaning | Example |
|-------|------|---------|---------|
| `EventType` | `SiemEventType` | Category of the security event. | `PasswordChanged` |
| `Outcome` | `string` | Human-readable outcome label. | `Success`, `AttemptStarted`, `Failed:InvalidCredentials` |
| `Username` | `string` | AD username / principal involved. | `jdoe` |
| `ClientIp` | `string?` | Remote client IP (optional). | `203.0.113.7` |
| `TraceId` | `string?` | Correlation/trace identifier for cross-log joining (optional). | `4bf92f3577b34da6a3ce929d0e0e4736` |
| `Detail` | `string?` | Free-form detail; must not contain secrets (optional). | `Wrong current password` |

### Redaction guarantee

The record has **no** secret-named fields by design — this is compile-time redaction. Adding a `Password`, `Token`, `PrivateKey`, `Secret`, or `ApiKey` property is prohibited and would break the build's enforcement tests:

- `AuditEventRedactionTests` — reflection test asserting no secret-shaped properties exist on the record (`src/PassReset.Tests.Windows/Web/Services/AuditEventRedactionTests.cs`).
- `AuditEventIntegrationTests` — asserts no plaintext password reaches `Detail` on the live emission path (`src/PassReset.Tests.Windows/Web/Services/AuditEventIntegrationTests.cs`).

## Correlation

`TraceId` comes from `Activity.Current?.TraceId` at request entry (falling back to `"unknown"` when no activity is present). All events emitted during a single password-change attempt share the same `TraceId`, so they can be joined across log lines.

`PasswordChangeAttemptStarted` is the **correlation anchor** — it is emitted first, at controller entry, before any validation. The controller also opens a request-scoped logging scope (`Username`, `TraceId`, `ClientIp`) so downstream provider, decorator, and email logs inherit the same identifiers.

## Syslog and SD-ID

When `SiemSettings.Syslog.Enabled = true`, events are emitted as RFC 5424 syslog lines. Structured events (the `LogEvent(AuditEvent)` path) carry an RFC 5424 STRUCTURED-DATA element whose SD-ID is taken from `SiemSettings.Syslog.SdId`.

| Key | Default | Notes |
|-----|---------|-------|
| `SiemSettings.Syslog.SdId` | `passreset@32473` | RFC 5424 SD-ID for the STRUCTURED-DATA element. Operators may override; validation rejects values longer than 32 chars or containing space, `=`, `]`, or `"`. |
| `SiemSettings.Syslog.Host` | `""` | Hostname / IP of the syslog collector. |
| `SiemSettings.Syslog.Port` | `514` | Collector port (`1..65535`). |
| `SiemSettings.Syslog.Protocol` | `UDP` | Transport: `UDP` or `TCP`. |

The structured element emits SD-PARAMs `event`, `outcome`, `user`, and — when non-null — `ip`, `traceId`, and `detail`. All values are escaped per RFC 5424.

## Email alerts

When `SiemSettings.AlertEmail.Enabled = true`, a filtered subset of events is also delivered by email through the configured `IEmailService`.

| Key | Default | Notes |
|-----|---------|-------|
| `SiemSettings.AlertEmail.Enabled` | `false` | Master switch for the email-alert channel. |
| `SiemSettings.AlertEmail.Recipients` | `[]` | One or more recipient addresses (each must contain `@`). |
| `SiemSettings.AlertEmail.AlertOnEvents` | `["PortalLockout"]` | Event-type names that trigger an email alert. Operators choose which `SiemEventType` values fire mail. Entries must map to valid `SiemEventType` enum members. |

Only events whose `SiemEventType` name appears in `AlertOnEvents` (case-insensitive) generate an alert email; all other events still flow to syslog if syslog is enabled.
