# PassReset

A self-service portal for Active Directory password operations. Users authenticate against AD and manage their own password without help-desk involvement.

## Language

**Password Change**:
An authenticated user supplies their current password and a new one; the portal binds to AD and changes *their own* password. The existing core flow.
_Avoid_: Reset (a Change is not a Reset — see below), update

**Password Reset**:
A user who has *forgotten* their password regains access via identity-proofing (a second factor), with the portal setting the password through a privileged path rather than the user authenticating with a current password. **Out of scope for v2.x; targeted for v3.0.** The product name anticipates this capability; the current product does not implement it.
_Avoid_: Recovery, forgot-password (use "Reset")

**Status Check**:
An authenticated, read-only interaction: the user binds with their current password and sees their password **expiry status** and the **live AD password policy**, without changing anything. New in v2.1. Scoped to expiry + policy (not full account state such as lockout or group membership). Served by a dedicated `/api/status` endpoint that reuses the change flow's authenticated bind, its enumeration-safe failure handling (generic response; precise codes kept only in SIEM), and its rate-limit + reCAPTCHA protections. Expiry is the per-user *resolved* value (`msDS-UserPasswordExpiryTimeComputed`), degrading to the domain default — with a stated caveat — when that attribute is unreadable.
_Avoid_: Account status, password status, lookup

**Status view**:
The screen that presents a Status Check result. In v2.1 it is the portal's landing screen, with Password Change offered as an action from it.

**Identity-Proofing**:
Proving a user's identity *without* their current password (e.g. email/SMS OTP, security questions, authenticator). The hard core of Password Reset. Does not exist in the codebase and is **not built in v2.x**.
_Avoid_: 2FA, MFA, second factor (those name mechanisms; "Identity-Proofing" names the capability)

**Provider Mode**:
The selector (`Auto` | `Windows` | `Ldap`) that decides which directory adapter backs a Change: the Windows `System.DirectoryServices.AccountManagement` path or the cross-platform `System.DirectoryServices.Protocols` LDAP path. `Auto` picks Windows on Windows, LDAP elsewhere.

## Provider Seams

The portal's interaction with Active Directory is exposed as three distinct responsibilities, each its own seam. A single directory adapter (chosen by [[Provider Mode]]) satisfies all three; only the Change seam is decorated (lockout, local policy).

**Password Changer**:
The credentialed write path: authenticate the user and change their own password. The only seam wrapped by the lockout and Local Policy decorators.
_Avoid_: PasswordChangeProvider (that is one adapter, not the seam)

**Password Status Reader**:
The credentialed read path serving a Status Check: authenticate and return resolved expiry plus the effective AD password policy. Read-only; never mutates.
_Avoid_: status provider, policy provider

**Directory User Reader**:
The unauthenticated read path used by side-effects (the password-changed email and the expiry-notification background service): resolve a user's email, enumerate a group's members, read the domain maximum password age. Reads directory facts without binding as the user.
_Avoid_: user lookup, directory query

**Local Policy**:
Offline password-validation rules applied *before* the AD round-trip: banned-word substring matching and an offline Pwned (HaveIBeenPwned) SHA-1 lookup against operator-supplied files. A validation layer, not a credential store.
_Avoid_: Local password database, local DB (the implementation stores no credentials despite the historical phase name "local-password-db")
