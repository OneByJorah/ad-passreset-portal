# Password Policy Display

When `ClientSettings.ShowAdPasswordPolicy` is enabled, the portal shows the
effective Active Directory password policy above the change-password form so
users know the rules before they type. The rules are read live from AD on each
page load (the panel fetches them on mount).

## What's displayed

The panel (`AdPasswordPolicyPanel`) renders one line per applicable rule, sourced
from the `PasswordPolicy` record (`MinLength`, `RequiresComplexity`,
`HistoryLength`, `MinAgeDays`, `MaxAgeDays`):

| Rule | Shown when | Exact wording |
| --- | --- | --- |
| Minimum length | always | `Minimum {minLength} characters` |
| Complexity | `requiresComplexity` is `true` | `Must include uppercase, lowercase, number, and symbol` |
| History | `historyLength > 0` | `Cannot reuse last {historyLength} passwords` |
| Minimum age | `minAgeDays > 0` | `Minimum age: {minAgeDays} day(s) before it can be changed again` |
| Maximum age (expiry) | `maxAgeDays > 0` | `Password expires after {maxAgeDays} days` |

Two age values are distinct:

- **`MinAgeDays`** — how long a password must exist before it can be changed
  **again** (AD's `minPwdAge`). A value of `0` means there is no minimum age, and
  the rule is hidden.
- **`MaxAgeDays`** — the expiry window: how long until the password must be
  changed (AD's `maxPwdAge`). A value of `0` means the domain enforces no expiry,
  and the rule is hidden.

## How the policy is determined per provider

Both providers read the **Default Domain Policy** only, and both degrade
gracefully — a failed lookup never throws, it just produces a less complete (or
empty) policy.

### Windows provider (`ProviderMode: Windows` or `Auto` on Windows)

Binds a single domain-root `DirectoryEntry` (via `System.DirectoryServices` —
either the automatic domain context or the configured LDAP host) and reads all
five attributes from that one object:

- `minPwdLength` → minimum length
- `pwdProperties` bit `0x1` (`DOMAIN_PASSWORD_COMPLEX`) → complexity requirement
- `pwdHistoryLength` → history length
- `minPwdAge` / `maxPwdAge` → minimum/maximum age

On any failure the whole read returns `null` (the panel then renders nothing).

### LDAP provider (`ProviderMode: Ldap`, cross-platform)

Splits the read across two queries:

1. **rootDSE** supplies `minPwdLength`, `minPwdAge`, and `maxPwdAge`.
2. A **Base-scope `(objectClass=domainDNS)` search on `defaultNamingContext`**
   (the domain root) supplies complexity and history:
   - `pwdProperties` bit `0x1` → `RequiresComplexity`
   - `pwdHistoryLength` → `HistoryLength`

The domain-root read in step 2 is best-effort: **any failure degrades complexity
to `false` and history to `0`** so the panel still renders length and age. A
bind or rootDSE failure returns `null`.

## Fine-grained password policies (FGPP / PSO)

Fine-Grained Password Policies (PSOs / `msDS-PasswordSettings`) are **not
supported by either provider**. The displayed policy is **always** the Default
Domain Policy.

A user governed by a PSO may therefore see a slightly inaccurate displayed
policy — but this is cosmetic only: **AD still enforces the user's real
(PSO) policy at change time**, so a non-conforming password is rejected by the
directory regardless of what the panel showed. FGPP/PSO support is tracked as a
future enhancement.

## Server-side enforcement note

Minimum password age is **also enforced server-side** before the change is
attempted (`PreCheckMinPwdAge` in the Windows provider). The displayed minimum-age
rule is a courtesy warning: it tells users in advance, rather than letting them
submit a too-soon change that AD would reject anyway.

## Endpoint

`GET /api/password/policy` returns the `PasswordPolicy` as JSON. The panel calls
it on mount when `ShowAdPasswordPolicy` is enabled.

The endpoint **fails closed**:

- Returns **404** when `ShowAdPasswordPolicy` is disabled, **or** when the AD
  policy query fails (returns `null`).
- The UI renders nothing on a 404 — the policy panel never shows partial or
  stale data.
