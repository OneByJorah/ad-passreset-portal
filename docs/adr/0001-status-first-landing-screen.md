# Status-first landing screen (v2.1)

Status: accepted

## Decision

In v2.1 the portal's landing screen becomes the **Status view** (a read-only Status Check), with **Password Change** offered as an action from it — rather than the Password Change form being the home page as it has been since v1.x. The user authenticates once with their current password, sees their expiry status and the live AD password policy, and changes their password only if they choose to.

## Why this is recorded

It is hard to reverse (it restructures the SPA's entry point and the API surface — `PasswordForm` is no longer the root component, and a status endpoint joins the change endpoint), surprising without context (a future reader sees the change form is *not* the landing screen and will wonder why), and the result of a real trade-off.

## Considered options

- **Keep Change as the front door**, surface expiry only *after* a submit (post-submit/inline). Lower cost, zero new pre-auth surface — rejected because it under-delivers the "see where I stand before acting" value the Status Check is meant to provide.
- **Two independent entry points** (separate Change and Check screens, each authenticating). Rejected: double authentication and a fragmented experience for no real gain.
- **Status-first, change-optional** (chosen): one authentication, status as the front door, change as an action.

## Consequences

- Change-only users navigate one extra step (status → change). Accepted as the cost of the more coherent "how long do I have? …ok, change it now" flow.
- The Change form gets expiry/policy context for free, since the Status Check already fetched it.
- Authentication for the Status Check reuses the existing current-password AD bind — no new authentication primitive is introduced (Identity-Proofing remains a v3.0 / Reset concern, deliberately not pulled forward).
