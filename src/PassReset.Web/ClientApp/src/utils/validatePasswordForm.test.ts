import { describe, it, expect } from 'vitest';
import { validatePasswordForm, type ValidateFields, type ValidateOptions } from './validatePasswordForm';

const filledFields: ValidateFields = {
  username: 'jdoe',
  currentPassword: 'OldPass1!',
  newPassword: 'NewPass1!',
  newPasswordVerify: 'NewPass1!',
};

const baseOptions: ValidateOptions = {
  attrs: undefined,
  emailRx: null,
  usernameRx: null,
  useEmail: false,
  minimumDistance: 0,
  messages: {},
};

describe('validatePasswordForm', () => {
  it('flags every empty field as required', () => {
    const errs = validatePasswordForm(
      { username: '', currentPassword: '', newPassword: '', newPasswordVerify: '' },
      { ...baseOptions, messages: { fieldRequired: 'Required.' } },
    );
    expect(errs).toEqual({
      username: 'Required.',
      currentPassword: 'Required.',
      newPassword: 'Required.',
      newPasswordVerify: 'Required.',
    });
  });

  it('treats whitespace-only username as empty', () => {
    const errs = validatePasswordForm({ ...filledFields, username: '   ' }, baseOptions);
    expect(errs.username).toBe('This field is required.');
  });

  it('returns no errors for a fully valid, distinct password pair', () => {
    expect(validatePasswordForm(filledFields, baseOptions)).toEqual({});
  });

  it('flags mismatch on the verify field when the two new passwords differ', () => {
    const errs = validatePasswordForm(
      { ...filledFields, newPasswordVerify: 'Different2@' },
      { ...baseOptions, messages: { passwordMatch: 'No match.' } },
    );
    expect(errs.newPasswordVerify).toBe('No match.');
    expect(errs.newPassword).toBeUndefined();
  });

  it('flags too-similar new password when under minimumDistance', () => {
    const errs = validatePasswordForm(
      { ...filledFields, currentPassword: 'Passw0rd!', newPassword: 'Passw0rd@', newPasswordVerify: 'Passw0rd@' },
      { ...baseOptions, minimumDistance: 5, messages: { distancePassword: 'Too similar.' } },
    );
    expect(errs.newPassword).toBe('Too similar.');
  });

  it('allows a sufficiently distant new password at minimumDistance', () => {
    const errs = validatePasswordForm(
      { ...filledFields, currentPassword: 'Passw0rd!', newPassword: 'TotallyOther9#', newPasswordVerify: 'TotallyOther9#' },
      { ...baseOptions, minimumDistance: 5 },
    );
    expect(errs.newPassword).toBeUndefined();
  });

  describe('username format branching', () => {
    const emailRx = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;
    const usernameRx = /^[a-z][a-z0-9]{2,}$/;

    it('requires email format when every allowed attribute is email-shaped', () => {
      const opts = { ...baseOptions, attrs: ['userprincipalname', 'mail'], emailRx };
      expect(validatePasswordForm({ ...filledFields, username: 'jdoe' }, opts).username).toMatch(/valid email/i);
      expect(validatePasswordForm({ ...filledFields, username: 'jdoe@corp.com' }, opts).username).toBeUndefined();
    });

    it('applies no regex when samaccountname is among the allowed attributes', () => {
      const opts = { ...baseOptions, attrs: ['samaccountname', 'mail'], emailRx };
      expect(validatePasswordForm({ ...filledFields, username: 'jdoe' }, opts).username).toBeUndefined();
      expect(validatePasswordForm({ ...filledFields, username: 'jdoe@corp.com' }, opts).username).toBeUndefined();
    });

    it('falls back to email regex via useEmail when no attrs are configured', () => {
      const opts = { ...baseOptions, attrs: undefined, useEmail: true, emailRx };
      expect(validatePasswordForm({ ...filledFields, username: 'nope' }, opts).username).toMatch(/valid email/i);
      expect(validatePasswordForm({ ...filledFields, username: 'a@b.co' }, opts).username).toBeUndefined();
    });

    it('falls back to username regex when neither attrs nor useEmail apply', () => {
      const opts = { ...baseOptions, attrs: undefined, useEmail: false, usernameRx };
      expect(validatePasswordForm({ ...filledFields, username: 'X' }, opts).username).toMatch(/valid username/i);
      expect(validatePasswordForm({ ...filledFields, username: 'jdoe' }, opts).username).toBeUndefined();
    });
  });
});
