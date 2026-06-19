import type { ApiErrorItem, ClientSettings, FormErrors } from '../types/settings';
import { ApiErrorCode } from '../types/settings';

/**
 * Resolve a single AD/server error code to its user-facing message, preferring the
 * operator-configured `alerts` string and falling back to a built-in default.
 *
 * Moved verbatim from PasswordForm; it is the private per-code helper of {@link mapApiErrors}.
 */
export function errorMessage(code: number, alerts: ClientSettings['alerts']): string {
  const a = alerts ?? {};
  switch (code) {
    case ApiErrorCode.FieldRequired:       return a.errorFieldRequired        ?? 'This field is required.';
    case ApiErrorCode.FieldMismatch:       return a.errorFieldMismatch        ?? 'Passwords do not match.';
    case ApiErrorCode.UserNotFound:        return a.errorInvalidUser          ?? 'User account not found.';
    case ApiErrorCode.InvalidCredentials:  return a.errorInvalidCredentials   ?? 'Current password is incorrect.';
    case ApiErrorCode.InvalidCaptcha:      return a.errorCaptcha              ?? 'Could not verify you are not a robot.';
    case ApiErrorCode.ChangeNotPermitted:  return a.errorPasswordChangeNotAllowed ?? 'Password change not allowed.';
    case ApiErrorCode.InvalidDomain:       return a.errorInvalidDomain        ?? 'Invalid domain.';
    case ApiErrorCode.LdapProblem:         return a.errorConnectionLdap       ?? 'Directory connection error.';
    case ApiErrorCode.ComplexPassword:     return a.errorComplexPassword      ?? 'Password does not meet complexity requirements.';
    case ApiErrorCode.MinimumScore:        return a.errorScorePassword        ?? 'Password is not strong enough.';
    case ApiErrorCode.MinimumDistance:     return a.errorDistancePassword     ?? 'New password is too similar to the current password.';
    case ApiErrorCode.PwnedPassword:       return a.errorPwnedPassword        ?? 'This password is publicly known. Please choose another.';
    case ApiErrorCode.PasswordTooYoung:    return a.errorPasswordTooYoung     ?? 'Password was changed too recently.';
    case ApiErrorCode.AccountDisabled:     return 'Your account is disabled. Contact IT Support.';
    case ApiErrorCode.RateLimitExceeded:        return a.errorRateLimitExceeded         ?? 'Too many attempts. Please wait and try again.';
    case ApiErrorCode.PwnedPasswordCheckFailed: return a.errorPwnedPasswordCheckFailed ?? 'Could not verify password safety. Please try again.';
    case ApiErrorCode.PortalLockout:            return a.errorPortalLockout            ?? 'Too many failed attempts. Please wait before trying again.';
    // ApproachingLockout uses the configured warning string as both the general error and warning banner.
    case ApiErrorCode.ApproachingLockout:       return a.errorApproachingLockout       ?? 'Incorrect password. One more failed attempt will temporarily lock your portal access.';
    case ApiErrorCode.PasswordTooRecentlyChanged: return a.errorPasswordTooRecentlyChanged ?? 'Your password was changed too recently. Please wait before trying again.';
    default:                                    return 'An unexpected error occurred. Please contact IT Support.';
  }
}

/**
 * Pure Server-Error Mapping: translate an AD/server `ApiResult.errors` list into the
 * field-keyed {@link FormErrors} shape produced by Form Validation. Each error is bucketed
 * by its `fieldName`; unfielded errors land in `general`. The last error per field wins,
 * matching the original in-component loop.
 *
 * Pure: it triggers no side effects. The approaching-lockout banner flag is derived by the
 * caller from the same error list (e.g. `errors.some(e => e.errorCode === ApproachingLockout)`).
 */
export function mapApiErrors(errors: ApiErrorItem[], alerts: ClientSettings['alerts']): FormErrors {
  const result: FormErrors = {};
  errors.forEach((err) => {
    const msg = errorMessage(err.errorCode, alerts);
    if (err.fieldName === 'Username')               result.username          = msg;
    else if (err.fieldName === 'CurrentPassword')   result.currentPassword   = msg;
    else if (err.fieldName === 'NewPassword')       result.newPassword       = msg;
    else if (err.fieldName === 'NewPasswordVerify') result.newPasswordVerify = msg;
    else                                            result.general           = msg;
  });
  return result;
}
