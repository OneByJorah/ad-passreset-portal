import { describe, it, expect } from 'vitest';
import { mapApiErrors } from './mapApiErrors';
import { ApiErrorCode } from '../types/settings';

describe('mapApiErrors', () => {
  it('buckets a fielded error onto its matching field', () => {
    const errs = mapApiErrors(
      [{ errorCode: ApiErrorCode.InvalidCredentials, fieldName: 'CurrentPassword' }],
      { errorInvalidCredentials: 'Wrong password.' },
    );
    expect(errs).toEqual({ currentPassword: 'Wrong password.' });
  });

  it('routes an unfielded error to general', () => {
    const errs = mapApiErrors([{ errorCode: ApiErrorCode.LdapProblem }], {});
    expect(errs).toEqual({ general: 'Directory connection error.' });
  });

  it('maps each known fieldName to its FormErrors key', () => {
    const errs = mapApiErrors(
      [
        { errorCode: ApiErrorCode.UserNotFound, fieldName: 'Username' },
        { errorCode: ApiErrorCode.InvalidCredentials, fieldName: 'CurrentPassword' },
        { errorCode: ApiErrorCode.ComplexPassword, fieldName: 'NewPassword' },
        { errorCode: ApiErrorCode.FieldMismatch, fieldName: 'NewPasswordVerify' },
      ],
      {},
    );
    expect(Object.keys(errs).sort()).toEqual(
      ['currentPassword', 'newPassword', 'newPasswordVerify', 'username'].sort(),
    );
  });

  it('prefers the operator-configured alert string over the built-in default', () => {
    expect(
      mapApiErrors([{ errorCode: ApiErrorCode.PwnedPassword }], { errorPwnedPassword: 'Custom pwned.' }).general,
    ).toBe('Custom pwned.');
    expect(mapApiErrors([{ errorCode: ApiErrorCode.PwnedPassword }], {}).general).toMatch(/publicly known/i);
  });

  it('falls back to the generic message for an unmapped code', () => {
    expect(mapApiErrors([{ errorCode: ApiErrorCode.BannedWord }], {}).general).toMatch(/unexpected error/i);
  });

  it('lets the last error per field win', () => {
    const errs = mapApiErrors(
      [
        { errorCode: ApiErrorCode.InvalidCredentials, fieldName: 'CurrentPassword' },
        { errorCode: ApiErrorCode.AccountDisabled, fieldName: 'CurrentPassword' },
      ],
      {},
    );
    expect(errs.currentPassword).toMatch(/disabled/i);
  });
});
