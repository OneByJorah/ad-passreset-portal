import { useState, useMemo } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import CircularProgress from '@mui/material/CircularProgress';
import IconButton from '@mui/material/IconButton';
import InputAdornment from '@mui/material/InputAdornment';
import TextField from '@mui/material/TextField';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import AutorenewIcon from '@mui/icons-material/Autorenew';
import Visibility from '@mui/icons-material/Visibility';
import VisibilityOff from '@mui/icons-material/VisibilityOff';
import { changePassword } from '../api/client';
import { usePolicy } from '../hooks/usePolicy';
import { useRecaptcha } from '../hooks/useRecaptcha';
import type { ClientSettings, FormErrors } from '../types/settings';
import { ApiErrorCode } from '../types/settings';
import { generatePassword } from '../utils/passwordGenerator';
import { validatePasswordForm } from '../utils/validatePasswordForm';
import { mapApiErrors } from '../utils/mapApiErrors';
import AdPasswordPolicyPanel from './AdPasswordPolicyPanel';
import ClipboardCountdown from './ClipboardCountdown';
import HibpIndicator from './HibpIndicator';
import { useHibpCheck } from '../hooks/useHibpCheck';
import { useClipboardGeneration } from '../hooks/useClipboardGeneration';
import { PasswordStrengthMeter } from './PasswordStrengthMeter';

interface Props {
  settings: ClientSettings;
  onSuccess: () => void;
  /** Pre-fills the username (e.g. carried over from the Status view). Defaults to empty. */
  initialUsername?: string;
}

export function PasswordForm({ settings, onSuccess, initialUsername = '' }: Props) {
  const form    = settings.changePasswordForm ?? {};
  const errors_ = settings.errorsPasswordForm ?? {};
  const regex   = settings.validationRegex    ?? {};

  // Build validation regexes once at mount. Try/catch guards against invalid patterns
  // in config — a bad pattern silently disables that check rather than breaking the form.
  const emailRx    = useMemo(() => { try { return regex.emailRegex    ? new RegExp(regex.emailRegex)    : null; } catch { return null; } }, [regex.emailRegex]);
  const usernameRx = useMemo(() => { try { return regex.usernameRegex ? new RegExp(regex.usernameRegex) : null; } catch { return null; } }, [regex.usernameRegex]);

  const attrs = settings.allowedUsernameAttributes;
  const usernameHint = useMemo(() => {
    if (!attrs || attrs.length === 0) return null;
    const parts = attrs.map(attr => {
      if (attr === 'samaccountname')    return 'username (e.g. jdoe)';
      if (attr === 'userprincipalname') return 'user principal name (e.g. jdoe@corp.com)';
      if (attr === 'mail')              return 'email address';
      return attr;
    });
    return 'Enter your ' + parts.join(' or ');
  }, [attrs]);

  const [username, setUsername]                 = useState(initialUsername);
  const [currentPassword, setCurrentPassword]   = useState('');
  const [newPassword, setNewPassword]           = useState('');
  const [newPasswordVerify, setNewPasswordVerify] = useState('');

  // FEAT-002: fetch effective AD password policy when the operator opts in.
  const { policy: adPolicy, loading: adPolicyLoading } = usePolicy(
    settings.showAdPasswordPolicy === true
  );

  const [showCurrent, setShowCurrent]           = useState(false);
  const [showNew, setShowNew]                   = useState(false);
  const [showVerify, setShowVerify]             = useState(false);

  const [formErrors, setFormErrors]             = useState<FormErrors>({});
  const [submitting, setSubmitting]             = useState(false);
  const [approachingLockout, setApproachingLockout] = useState(false);

  // FEAT-003: clipboard auto-clear lifecycle, owned by the hook.
  const {
    remaining: clipboardRemaining,
    state: clipboardState,
    copyAndSchedule,
    cancel: cancelClipboard,
  } = useClipboardGeneration(settings.clipboardClearSeconds ?? 30);

  const { executeRecaptcha } = useRecaptcha(
    settings.recaptcha?.enabled ? settings.recaptcha.siteKey : undefined
  );

  // FEAT-004: HIBP blur-triggered breach indicator. Debounced at 400ms,
  // AbortController-cancelled on subsequent blurs. Plaintext never leaves the
  // browser — only the 5-char SHA-1 prefix is POSTed to the server.
  const { state: hibpState, count: hibpCount, check: hibpCheck } = useHibpCheck(400);
  // Fail-open flag mirrors the server-side PasswordChangeOptions. Default TRUE
  // (hide indicator on HIBP outage) unless operator explicitly sets it to false,
  // in which case the warning Alert is rendered.
  const hibpFailOpen = settings.failOpenOnPwnedCheckUnavailable !== false;

  // Assemble the Form Validation inputs from settings + the compiled regexes.
  // Regex compilation (with bad-pattern guarding) stays in this component; the
  // validator receives already-compiled RegExp objects and stays pure.
  function validate(): FormErrors {
    return validatePasswordForm(
      { username, currentPassword, newPassword, newPasswordVerify },
      {
        attrs,
        emailRx,
        usernameRx,
        useEmail: settings.useEmail,
        minimumDistance: settings.minimumDistance,
        messages: {
          fieldRequired: errors_.fieldRequired,
          usernameEmailPattern: errors_.usernameEmailPattern,
          usernamePattern: errors_.usernamePattern,
          passwordMatch: errors_.passwordMatch,
          distancePassword: settings.alerts?.errorDistancePassword,
        },
      },
    );
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    const errs = validate();
    setFormErrors(errs);
    if (Object.keys(errs).length > 0) return;

    setSubmitting(true);
    setApproachingLockout(false);
    // Cancel any pending clipboard-clear timer — form submission supersedes it.
    cancelClipboard();
    try {
      const recaptchaToken = settings.recaptcha?.enabled && settings.recaptcha?.siteKey
        ? await executeRecaptcha()
        : '';

      const result = await changePassword({
        username,
        currentPassword,
        newPassword,
        newPasswordVerify,
        recaptcha: recaptchaToken,
      });

      if (!result.errors || result.errors.length === 0) {
        onSuccess();
        return;
      }

      // The approaching-lockout banner is derived from the same error list;
      // mapApiErrors itself stays a pure mapping with no side effects.
      if (result.errors.some(err => err.errorCode === ApiErrorCode.ApproachingLockout)) {
        setApproachingLockout(true);
      }
      setFormErrors(mapApiErrors(result.errors, settings.alerts));
    } catch {
      setFormErrors({ general: 'An unexpected error occurred. Please try again.' });
    } finally {
      setSubmitting(false);
    }
  }

  async function handleGenerate() {
    const pwd = generatePassword(settings.passwordEntropy || 16);
    setNewPassword(pwd);
    setNewPasswordVerify(pwd);
    setShowNew(true);
    setShowVerify(true);

    // FEAT-003: hand the generated password to the clipboard hook, which copies it
    // and schedules the auto-clear countdown (cancelling any prior pending clear).
    await copyAndSchedule(pwd);
  }

  const visibilityAdornment = (show: boolean, toggle: () => void) => (
    <InputAdornment position="end">
      <IconButton onClick={toggle} edge="end" aria-label={show ? 'Hide password' : 'Show password'}>
        {show ? <VisibilityOff /> : <Visibility />}
      </IconButton>
    </InputAdornment>
  );

  return (
    <Box component="form" onSubmit={handleSubmit} noValidate>
      {form.helpText && (
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          {form.helpText}
        </Typography>
      )}

      {/* AD password policy panel (FEAT-002 / STAB-021) — rendered above Username by default */}
      {settings.showAdPasswordPolicy && (
        <AdPasswordPolicyPanel policy={adPolicy} loading={adPolicyLoading} />
      )}

      {/* Username */}
      <TextField
        fullWidth
        required
        label={form.usernameLabel ?? 'Username'}
        helperText={formErrors.username ?? (usernameHint !== null
          ? usernameHint
          : (settings.useEmail
            ? (form.usernameHelpblock ?? 'Your organisation email address')
            : (form.usernameDefaultDomainHelperBlock ?? form.usernameHelpblock ?? '')))}
        error={!!formErrors.username}
        value={username}
        onChange={e => setUsername(e.target.value)}
        autoComplete="username"
        inputProps={{ maxLength: 256 }}
        sx={{ mb: 2 }}
      />

      {/* Current password */}
      <TextField
        fullWidth
        required
        label={form.currentPasswordLabel ?? 'Current Password'}
        helperText={formErrors.currentPassword ?? (form.currentPasswordHelpblock ?? '')}
        error={!!formErrors.currentPassword}
        type={showCurrent ? 'text' : 'password'}
        value={currentPassword}
        onChange={e => setCurrentPassword(e.target.value)}
        autoComplete="current-password"
        inputProps={{ maxLength: 256 }}
        InputProps={{ endAdornment: visibilityAdornment(showCurrent, () => setShowCurrent(v => !v)) }}
        sx={{ mb: 2 }}
      />

      {/* New password */}
      <TextField
        fullWidth
        required
        label={form.newPasswordLabel ?? 'New Password'}
        helperText={formErrors.newPassword ?? (form.newPasswordHelpblock ?? '')}
        error={!!formErrors.newPassword}
        type={showNew ? 'text' : 'password'}
        value={newPassword}
        onChange={e => setNewPassword(e.target.value)}
        onBlur={e => hibpCheck(e.target.value)}
        autoComplete="new-password"
        inputProps={{ maxLength: 256 }}
        InputProps={{
          endAdornment: (
            <InputAdornment position="end">
              {settings.usePasswordGeneration && (
                <Tooltip title="Generate password">
                  <IconButton onClick={handleGenerate} edge="end" aria-label="Generate password">
                    <AutorenewIcon />
                  </IconButton>
                </Tooltip>
              )}
              <IconButton onClick={() => setShowNew(v => !v)} edge="end" aria-label={showNew ? 'Hide password' : 'Show password'}>
                {showNew ? <VisibilityOff /> : <Visibility />}
              </IconButton>
            </InputAdornment>
          ),
        }}
        sx={{ mb: settings.showPasswordMeter ? 0.5 : 2 }}
      />

      {/* FEAT-004: HIBP breach indicator (blur-triggered, debounced, k-anonymity) */}
      <HibpIndicator state={hibpState} count={hibpCount} failOpen={hibpFailOpen} />

      {/* FEAT-003: clipboard auto-clear countdown / cleared chip */}
      <ClipboardCountdown remaining={clipboardRemaining} state={clipboardState} />

      {settings.showPasswordMeter && (
        <Box sx={{ mb: 2 }}>
          <PasswordStrengthMeter password={newPassword} />
        </Box>
      )}

      {/* Confirm new password */}
      <TextField
        fullWidth
        required
        label={form.newPasswordVerifyLabel ?? 'Re-enter New Password'}
        helperText={formErrors.newPasswordVerify ?? (form.newPasswordVerifyHelpblock ?? '')}
        error={!!formErrors.newPasswordVerify}
        type={showVerify ? 'text' : 'password'}
        value={newPasswordVerify}
        onChange={e => setNewPasswordVerify(e.target.value)}
        autoComplete="new-password"
        inputProps={{ maxLength: 256 }}
        InputProps={{ endAdornment: visibilityAdornment(showVerify, () => setShowVerify(v => !v)) }}
        sx={{ mb: 3 }}
      />

      {/* Live region for screen reader announcements of dynamic errors */}
      <Box aria-live="assertive" aria-atomic="true">
        {/* Approaching-lockout warning — shown when the next failure will trigger portal lockout */}
        {approachingLockout && (
          <Alert severity="warning" sx={{ mb: 2 }}>
            {settings.alerts?.errorApproachingLockout
              ?? 'Warning: one more failed attempt will temporarily lock your access to this portal.'}
          </Alert>
        )}

        {/* General error */}
        {formErrors.general && (
          <Alert severity="error" sx={{ mb: 2 }}>
            {formErrors.general}
          </Alert>
        )}
      </Box>

      <Button
        type="submit"
        variant="contained"
        fullWidth
        size="large"
        disabled={submitting}
        startIcon={submitting ? <CircularProgress size={18} color="inherit" /> : undefined}
      >
        {submitting ? 'Changing…' : (form.changePasswordButtonLabel ?? 'Change Password')}
      </Button>
    </Box>
  );
}
