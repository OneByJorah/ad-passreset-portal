import { describe, it, expect, afterEach, vi } from 'vitest';
import { mockFetchOnce } from '../test-utils/fetchMock';
import { checkStatus } from './client';
import type { StatusResponse } from '../types/settings';

const successBody: StatusResponse = {
  authenticated: true,
  expiresUtc: '2026-09-01T00:00:00Z',
  neverExpires: false,
  source: 'Resolved',
  policy: {
    minLength: 8,
    requiresComplexity: true,
    historyLength: 5,
    minAgeDays: 1,
    maxAgeDays: 90,
  },
};

describe('checkStatus', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('returns StatusResponse with authenticated===true on 200 JSON', async () => {
    mockFetchOnce(successBody);
    const result = await checkStatus({ username: 'alice', currentPassword: 'pw', recaptcha: '' });

    expect('authenticated' in result).toBe(true);
    const ok = result as StatusResponse;
    expect(ok.authenticated).toBe(true);
    expect(ok.source).toBe('Resolved');
    expect(ok.expiresUtc).toBe('2026-09-01T00:00:00Z');
    expect(ok.policy?.minLength).toBe(8);
  });

  it('returns ApiResult with errors on 400 JSON failure', async () => {
    mockFetchOnce({ errors: [{ errorCode: 4 }] }, { status: 400 });
    const result = await checkStatus({ username: 'nope', currentPassword: 'x', recaptcha: '' });

    expect('errors' in result).toBe(true);
    const fail = result as { errors: { errorCode: number }[] };
    expect(fail.errors[0].errorCode).toBe(4);
  });

  it('returns ApiResult with errorCode 15 on 429', async () => {
    mockFetchOnce({}, { status: 429 });
    const result = await checkStatus({ username: 'alice', currentPassword: 'pw', recaptcha: '' });

    expect('errors' in result).toBe(true);
    const fail = result as { errors: { errorCode: number }[] };
    expect(fail.errors[0].errorCode).toBe(15);
  });

  it('returns ApiResult with errorCode 0 on non-JSON response', async () => {
    mockFetchOnce('<html>error</html>', { status: 500, contentType: 'text/html' });
    const result = await checkStatus({ username: 'alice', currentPassword: 'pw', recaptcha: '' });

    expect('errors' in result).toBe(true);
    const fail = result as { errors: { errorCode: number }[] };
    expect(fail.errors[0].errorCode).toBe(0);
  });

  it('sends POST to /api/password/status with JSON body', async () => {
    const fetchFn = mockFetchOnce(successBody);
    await checkStatus({ username: 'bob', currentPassword: 'secret', recaptcha: 'token' });

    expect(fetchFn).toHaveBeenCalledWith('/api/password/status', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'bob', currentPassword: 'secret', recaptcha: 'token' }),
    });
  });
});
