import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useClipboardGeneration } from './useClipboardGeneration';

function stubClipboard(store: { text: string }) {
  vi.stubGlobal('navigator', {
    clipboard: {
      writeText: vi.fn(async (t: string) => { store.text = t; }),
      readText: vi.fn(async () => store.text),
    },
  });
}

describe('useClipboardGeneration', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('starts idle, then counts down after copyAndSchedule', async () => {
    const store = { text: '' };
    stubClipboard(store);
    const { result } = renderHook(() => useClipboardGeneration(30));

    expect(result.current.state).toBe('idle');

    await act(async () => {
      await result.current.copyAndSchedule('Generated9#');
    });

    expect(store.text).toBe('Generated9#');
    expect(result.current.state).toBe('counting');
    expect(result.current.remaining).toBe(30);
  });

  it('progresses counting -> cleared -> idle and wipes the clipboard', async () => {
    const store = { text: '' };
    stubClipboard(store);
    const { result } = renderHook(() => useClipboardGeneration(3));

    await act(async () => { await result.current.copyAndSchedule('Secret9#'); });
    expect(result.current.state).toBe('counting');

    // Advance the 3s countdown; the interval fires the async performClear at 0.
    await act(async () => { await vi.advanceTimersByTimeAsync(3000); });
    expect(result.current.state).toBe('cleared');
    expect(store.text).toBe(''); // readback matched -> wiped

    // After the 2s reset window it returns to idle.
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(result.current.state).toBe('idle');
  });

  it('does not wipe the clipboard if its contents changed after scheduling', async () => {
    const store = { text: '' };
    stubClipboard(store);
    const { result } = renderHook(() => useClipboardGeneration(2));

    await act(async () => { await result.current.copyAndSchedule('Secret9#'); });
    store.text = 'user-copied-something-else';

    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(store.text).toBe('user-copied-something-else');
  });

  it('cancel() stops the countdown and returns to idle (matching submit-cancel behaviour)', async () => {
    const store = { text: '' };
    stubClipboard(store);
    const { result } = renderHook(() => useClipboardGeneration(30));

    await act(async () => { await result.current.copyAndSchedule('Secret9#'); });
    act(() => { result.current.cancel(); });

    // The component's submit path cancels the handle then forces idle; preserve that.
    expect(result.current.state).toBe('idle');
    // No clear fires after cancel.
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
    expect(store.text).toBe('Secret9#');
  });

  it('stays idle when the Clipboard API is unavailable', async () => {
    vi.stubGlobal('navigator', {}); // no clipboard
    const { result } = renderHook(() => useClipboardGeneration(30));

    await act(async () => { await result.current.copyAndSchedule('Secret9#'); });
    expect(result.current.state).toBe('idle');
  });

  it('cancels a pending timer on unmount', async () => {
    const store = { text: '' };
    stubClipboard(store);
    const { result, unmount } = renderHook(() => useClipboardGeneration(30));

    await act(async () => { await result.current.copyAndSchedule('Secret9#'); });
    unmount();

    // The clear must not fire after unmount.
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
    expect(store.text).toBe('Secret9#');
  });
});
