import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import App from './App';
import * as client from './api/client';
import type { ClientSettings } from './types/settings';

// Minimal loaded settings: reCAPTCHA disabled, no policy panel — enough to render
// StatusView (landing) and PasswordForm (change step) without network policy fetches.
const settings = {
  recaptcha: { enabled: false },
  minimumDistance: 0,
  showAdPasswordPolicy: false,
} as unknown as ClientSettings;

afterEach(() => vi.restoreAllMocks());

describe('App landing screen (status-first; ADR-0001)', () => {
  it('renders the StatusView as the landing screen, not the PasswordForm', async () => {
    vi.spyOn(client, 'fetchSettings').mockResolvedValue(settings);

    render(<App />);

    // The Status view's primary action is "Check status".
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /check status/i })).toBeInTheDocument(),
    );

    // The PasswordForm's new-password fields must NOT be on the landing screen.
    expect(screen.queryByLabelText(/^new password/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/re-enter new password/i)).not.toBeInTheDocument();
  });

  it('shows the PasswordForm after a successful status check and clicking "Change password"', async () => {
    vi.spyOn(client, 'fetchSettings').mockResolvedValue(settings);
    const future = new Date(Date.now() + 12 * 24 * 60 * 60 * 1000).toISOString();
    vi.spyOn(client, 'checkStatus').mockResolvedValue({
      authenticated: true,
      expiresUtc: future,
      neverExpires: false,
      source: 'Resolved',
      policy: null,
    });

    render(<App />);

    await waitFor(() =>
      expect(screen.getByRole('button', { name: /check status/i })).toBeInTheDocument(),
    );

    // Run a status check from the landing view.
    fireEvent.change(screen.getByLabelText(/username/i), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'pw' } });
    fireEvent.click(screen.getByRole('button', { name: /check status/i }));

    // Status result shows a "Change password" action — click it to enter the change step.
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /change password/i })).toBeInTheDocument(),
    );
    fireEvent.click(screen.getByRole('button', { name: /change password/i }));

    // Now the PasswordForm's new-password fields are visible.
    await waitFor(() =>
      expect(screen.getByLabelText(/^new password/i)).toBeInTheDocument(),
    );
    expect(screen.getByLabelText(/re-enter new password/i)).toBeInTheDocument();
  });
});
