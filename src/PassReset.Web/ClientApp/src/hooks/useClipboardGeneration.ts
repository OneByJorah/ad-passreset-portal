import { useCallback, useEffect, useRef, useState } from 'react';
import { scheduleClipboardClear, type ClipboardClearHandle } from '../utils/clipboardClear';

/** Lifecycle phase of the clipboard auto-clear countdown. */
export type ClipboardState = 'idle' | 'counting' | 'cleared' | 'cancelled';

export interface ClipboardGeneration {
  /** Seconds left on the active countdown (0 when not counting). */
  remaining: number;
  /** Current lifecycle phase. */
  state: ClipboardState;
  /**
   * Copy `pwd` to the clipboard and schedule an auto-clear after `clearSeconds`.
   * No-ops gracefully when the Clipboard API is unavailable or the write fails.
   * Cancels any prior pending clear first (regenerate case).
   */
  copyAndSchedule: (pwd: string) => Promise<void>;
  /** Cancel any pending clear (e.g. when a Change is submitted). */
  cancel: () => void;
}

/**
 * Clipboard Generation hook: owns the copy-to-clipboard + auto-clear countdown lifecycle
 * for a generated password. Its boundary is the *clipboard* — choosing and filling the new
 * password remains the caller's job; the caller invokes {@link ClipboardGeneration.copyAndSchedule}
 * after filling the fields.
 *
 * @param clearSeconds Delay before auto-clear. 0 or negative copies without scheduling.
 */
export function useClipboardGeneration(clearSeconds: number): ClipboardGeneration {
  const [remaining, setRemaining] = useState<number>(0);
  const [state, setState] = useState<ClipboardState>('idle');
  const handleRef = useRef<ClipboardClearHandle | null>(null);
  const clearedResetTimerRef = useRef<number | null>(null);

  // Cancel any pending timer when the consuming component unmounts.
  useEffect(() => {
    return () => {
      handleRef.current?.cancel();
      if (clearedResetTimerRef.current !== null) {
        window.clearTimeout(clearedResetTimerRef.current);
      }
    };
  }, []);

  const cancel = useCallback(() => {
    handleRef.current?.cancel();
    handleRef.current = null;
    setState('idle');
  }, []);

  const copyAndSchedule = useCallback(async (pwd: string) => {
    // Cancel any prior timer first (regenerate case) so the old countdown
    // does not race the new password's clear timer.
    handleRef.current?.cancel();
    handleRef.current = null;
    if (clearedResetTimerRef.current !== null) {
      window.clearTimeout(clearedResetTimerRef.current);
      clearedResetTimerRef.current = null;
    }

    try {
      if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(pwd);
      } else {
        // Clipboard API unavailable — skip scheduling entirely.
        setState('idle');
        return;
      }
    } catch {
      // Write failed (permission denied, insecure context) — do not schedule.
      setState('idle');
      return;
    }

    if (clearSeconds > 0) {
      setState('counting');
      setRemaining(clearSeconds);
      handleRef.current = scheduleClipboardClear(
        pwd,
        clearSeconds,
        (r) => setRemaining(r),
        () => {
          setState('cleared');
          if (clearedResetTimerRef.current !== null) {
            window.clearTimeout(clearedResetTimerRef.current);
          }
          clearedResetTimerRef.current = window.setTimeout(() => {
            setState('idle');
            clearedResetTimerRef.current = null;
          }, 2000);
        },
        () => setState('cancelled'),
      );
    } else {
      setState('idle');
    }
  }, [clearSeconds]);

  return { remaining, state, copyAndSchedule, cancel };
}
