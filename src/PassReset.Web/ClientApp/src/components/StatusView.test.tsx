import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { StatusView } from './StatusView';
import * as client from '../api/client';
import type { ClientSettings } from '../types/settings';

const settings = { recaptcha: { enabled: false } } as unknown as ClientSettings;

afterEach(() => vi.restoreAllMocks());

describe('StatusView', () => {
  it('shows resolved expiry and a change-password action on success', async () => {
    const future = new Date(Date.now() + 12 * 24 * 60 * 60 * 1000).toISOString();
    vi.spyOn(client, 'checkStatus').mockResolvedValue({
      authenticated: true,
      expiresUtc: future,
      neverExpires: false,
      source: 'Resolved',
      policy: null,
    });

    render(<StatusView settings={settings} onChangePassword={() => {}} />);
    fireEvent.change(screen.getByLabelText(/username/i), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'pw' } });
    fireEvent.click(screen.getByRole('button', { name: /check status/i }));

    await waitFor(() => expect(screen.getByText(/expires/i)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /change password/i })).toBeInTheDocument();
  });

  it('shows "does not expire" text when neverExpires is true', async () => {
    vi.spyOn(client, 'checkStatus').mockResolvedValue({
      authenticated: true,
      expiresUtc: null,
      neverExpires: true,
      source: 'Resolved',
      policy: null,
    });

    render(<StatusView settings={settings} onChangePassword={() => {}} />);
    fireEvent.change(screen.getByLabelText(/username/i), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'pw' } });
    fireEvent.click(screen.getByRole('button', { name: /check status/i }));

    await waitFor(() => expect(screen.getByText(/does not expire/i)).toBeInTheDocument());
  });

  it('appends the domain-default caveat when source is DomainDefault', async () => {
    const future = new Date(Date.now() + 5 * 24 * 60 * 60 * 1000).toISOString();
    vi.spyOn(client, 'checkStatus').mockResolvedValue({
      authenticated: true,
      expiresUtc: future,
      neverExpires: false,
      source: 'DomainDefault',
      policy: null,
    });

    render(<StatusView settings={settings} onChangePassword={() => {}} />);
    fireEvent.change(screen.getByLabelText(/username/i), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'pw' } });
    fireEvent.click(screen.getByRole('button', { name: /check status/i }));

    await waitFor(() => expect(screen.getByText(/domain default policy/i)).toBeInTheDocument());
  });

  it('calls onChangePassword with the username when the action is clicked', async () => {
    const future = new Date(Date.now() + 3 * 24 * 60 * 60 * 1000).toISOString();
    vi.spyOn(client, 'checkStatus').mockResolvedValue({
      authenticated: true,
      expiresUtc: future,
      neverExpires: false,
      source: 'Resolved',
      policy: null,
    });
    const onChangePassword = vi.fn();

    render(<StatusView settings={settings} onChangePassword={onChangePassword} />);
    fireEvent.change(screen.getByLabelText(/username/i), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'pw' } });
    fireEvent.click(screen.getByRole('button', { name: /check status/i }));

    await waitFor(() => expect(screen.getByRole('button', { name: /change password/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /change password/i }));
    expect(onChangePassword).toHaveBeenCalledWith('alice');
  });

  it('shows a single generic error on failure (no enumeration)', async () => {
    vi.spyOn(client, 'checkStatus').mockResolvedValue({ errors: [{ errorCode: 0 }] });

    render(<StatusView settings={settings} onChangePassword={() => {}} />);
    fireEvent.change(screen.getByLabelText(/username/i), { target: { value: 'nope' } });
    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: /check status/i }));

    await waitFor(() => expect(screen.getByText(/invalid username or password/i)).toBeInTheDocument());
    expect(screen.queryByText(/expires/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /change password/i })).not.toBeInTheDocument();
  });
});
