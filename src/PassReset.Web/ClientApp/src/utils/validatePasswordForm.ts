import type { FormErrors } from '../types/settings';
import { levenshtein } from './levenshtein';

/** The four user-entered values a Password Change form validates. */
export interface ValidateFields {
  username: string;
  currentPassword: string;
  newPassword: string;
  newPasswordVerify: string;
}

/** Message strings used by validation, mirroring the `errorsPasswordForm` config block. */
export interface ValidateMessages {
  fieldRequired?: string;
  usernameEmailPattern?: string;
  usernamePattern?: string;
  passwordMatch?: string;
  /** Distance message — sourced from `alerts.errorDistancePassword` in the component. */
  distancePassword?: string;
}

/**
 * Flat settings subset the validator needs. Regexes arrive already compiled
 * (the component owns compilation + bad-pattern guarding); the validator stays pure.
 */
export interface ValidateOptions {
  attrs: string[] | undefined;
  emailRx: RegExp | null;
  usernameRx: RegExp | null;
  useEmail: boolean;
  minimumDistance: number;
  messages: ValidateMessages;
}

/**
 * Pure, synchronous client-side Form Validation for a Password Change.
 * Returns a field-keyed {@link FormErrors}; an empty object means no errors.
 *
 * Behaviour mirrors the former in-component `validate()` exactly: required fields,
 * username-format branching (per allowed attributes / useEmail), new-password match,
 * and minimum Levenshtein distance from the current password.
 */
export function validatePasswordForm(fields: ValidateFields, opts: ValidateOptions): FormErrors {
  const { username, currentPassword, newPassword, newPasswordVerify } = fields;
  const { attrs, emailRx, usernameRx, useEmail, minimumDistance, messages } = opts;

  const errs: FormErrors = {};
  const required = messages.fieldRequired ?? 'This field is required.';

  if (!username.trim())   { errs.username          = required; }
  if (!currentPassword)   { errs.currentPassword   = required; }
  if (!newPassword)       { errs.newPassword       = required; }
  if (!newPasswordVerify) { errs.newPasswordVerify = required; }

  if (username && attrs && attrs.length > 0) {
    // Apply email regex only when every configured attribute requires an email-format input.
    const allRequireEmail = attrs.every(a => a === 'userprincipalname' || a === 'mail');
    if (allRequireEmail && emailRx) {
      if (!emailRx.test(username))
        errs.username = messages.usernameEmailPattern ?? 'Please enter a valid email address.';
    }
    // samaccountname (or any combo including it): no regex — bare name and email-format both accepted
  } else if (username && useEmail && emailRx) {
    if (!emailRx.test(username))
      errs.username = messages.usernameEmailPattern ?? 'Please enter a valid email address.';
  } else if (username && usernameRx) {
    if (!usernameRx.test(username))
      errs.username = messages.usernamePattern ?? 'Please enter a valid username.';
  }

  if (newPassword && newPasswordVerify && newPassword !== newPasswordVerify)
    errs.newPasswordVerify = messages.passwordMatch ?? 'Passwords do not match.';

  if (newPassword && currentPassword && minimumDistance > 0) {
    if (levenshtein(currentPassword, newPassword) < minimumDistance)
      errs.newPassword = messages.distancePassword ?? 'New password is too similar to your current password.';
  }

  return errs;
}
