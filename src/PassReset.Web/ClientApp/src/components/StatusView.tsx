import { useState } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import CircularProgress from '@mui/material/CircularProgress';
import IconButton from '@mui/material/IconButton';
import InputAdornment from '@mui/material/InputAdornment';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import Visibility from '@mui/icons-material/Visibility';
import VisibilityOff from '@mui/icons-material/VisibilityOff';
import { checkStatus } from '../api/client';
import { useRecaptcha } from '../hooks/useRecaptcha';
import type { ClientSettings, StatusResponse } from '../types/settings';
import AdPasswordPolicyPanel from './AdPasswordPolicyPanel';

interface Props {
  settings: ClientSettings;
  onChangePassword: (username: string) => void;
}

// Enumeration-safe: a single fixed string for every failure. Mirrors the server's
// STAB-013 redaction — never reveal whether the account exists or the password was wrong.
const GENERIC_ERROR = 'Invalid username or password.';

const DOMAIN_DEFAULT_CAVEAT =
  ' (based on the domain default policy — your account may have a specific policy)';

/**
 * Humanises the resolved expiry into a single sentence.
 * - neverExpires: a fixed never-expires sentence.
 * - otherwise: localized date + whole days remaining, with the DomainDefault caveat appended
 *   when the expiry was degraded to the domain default.
 */
function expiryText(status: StatusResponse): string {
  if (status.neverExpires) return 'Your password does not expire';
  if (!status.expiresUtc) return 'Your password expiry could not be determined';

  const expires = new Date(status.expiresUtc);
  const localized = expires.toLocaleDateString();
  const days = Math.ceil((expires.getTime() - Date.now()) / (24 * 60 * 60 * 1000));
  let text = `Your password expires on ${localized} (${days} days)`;
  if (status.source === 'DomainDefault') text += DOMAIN_DEFAULT_CAVEAT;
  return text;
}

export function StatusView({ settings, onChangePassword }: Props) {
  const form = settings.changePasswordForm ?? {};

  const [username, setUsername] = useState('');
  const [currentPassword, setCurrentPassword] = useState('');
  const [showCurrent, setShowCurrent] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<StatusResponse | null>(null);

  const { executeRecaptcha } = useRecaptcha(
    settings.recaptcha?.enabled ? settings.recaptcha.siteKey : undefined,
  );

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setStatus(null);
    setSubmitting(true);
    try {
      const recaptcha = settings.recaptcha?.enabled && settings.recaptcha?.siteKey
        ? await executeRecaptcha()
        : '';

      const result = await checkStatus({ username, currentPassword, recaptcha });

      if ('authenticated' in result && result.authenticated) {
        setStatus(result);
      } else {
        // Any non-success collapses to the single generic message — no error-code branching.
        setError(GENERIC_ERROR);
      }
    } catch {
      setError(GENERIC_ERROR);
    } finally {
      setSubmitting(false);
    }
  }

  const visibilityAdornment = (
    <InputAdornment position="end">
      <IconButton
        onClick={() => setShowCurrent(v => !v)}
        edge="end"
        aria-label={showCurrent ? 'Hide password' : 'Show password'}
      >
        {showCurrent ? <VisibilityOff /> : <Visibility />}
      </IconButton>
    </InputAdornment>
  );

  if (status) {
    return (
      <Box>
        <Typography variant="body1" sx={{ mb: 2 }}>
          {expiryText(status)}
        </Typography>

        <AdPasswordPolicyPanel policy={status.policy} loading={false} />

        <Button
          variant="contained"
          fullWidth
          size="large"
          onClick={() => onChangePassword(username)}
        >
          Change password
        </Button>
      </Box>
    );
  }

  return (
    <Box component="form" onSubmit={handleSubmit} noValidate>
      <Stack spacing={2}>
        <TextField
          fullWidth
          required
          label={form.usernameLabel ?? 'Username'}
          value={username}
          onChange={e => setUsername(e.target.value)}
          autoComplete="username"
          inputProps={{ maxLength: 256 }}
        />

        <TextField
          fullWidth
          required
          label={form.currentPasswordLabel ?? 'Current password'}
          type={showCurrent ? 'text' : 'password'}
          value={currentPassword}
          onChange={e => setCurrentPassword(e.target.value)}
          autoComplete="current-password"
          inputProps={{ maxLength: 256 }}
          InputProps={{ endAdornment: visibilityAdornment }}
        />

        {/* Live region so screen readers announce the generic failure */}
        <Box aria-live="assertive" aria-atomic="true">
          {error && <Alert severity="error">{error}</Alert>}
        </Box>

        <Button
          type="submit"
          variant="contained"
          fullWidth
          size="large"
          disabled={submitting}
          startIcon={submitting ? <CircularProgress size={18} color="inherit" /> : undefined}
        >
          {submitting ? 'Checking…' : 'Check status'}
        </Button>
      </Stack>
    </Box>
  );
}
