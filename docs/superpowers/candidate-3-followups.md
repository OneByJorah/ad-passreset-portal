# Follow-ups — deferred from Candidate 3 (PasswordForm seam extraction)

These are real but OUT OF SCOPE for the behavior-preserving seam extraction.
Do not fold them into that refactor; they become visible once the pure functions are split out.

## FU-1: Divergent error-message fallbacks in PasswordForm
When validate() and errorMessage() become separate pure functions (validatePasswordForm / mapApiErrors),
these inconsistencies sit side-by-side and should be reconciled in a follow-up:
- minimumDistance fallback string differs: validate() uses "...too similar to your current password."
  (PasswordForm.tsx:174) vs errorMessage() uses "...too similar to the current password." (PasswordForm.tsx:56).
- required-field fallback reads from two different settings sub-objects: validate() uses errors_.fieldRequired,
  errorMessage() uses alerts.errorFieldRequired — same concept, two keys.
Decide a single source of truth per message and one fallback string.
