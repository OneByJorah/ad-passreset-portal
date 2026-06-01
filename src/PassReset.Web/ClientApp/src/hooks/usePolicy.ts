import { useEffect, useState } from 'react';

import { fetchPolicy } from '../api/client';
import type { PolicyResponse } from '../types/settings';

export function usePolicy(enabled: boolean) {
  const [policy, setPolicy] = useState<PolicyResponse | null>(null);
  const [fetched, setFetched] = useState(false);

  useEffect(() => {
    if (!enabled) return;

    let cancelled = false;
    fetchPolicy().then((p) => {
      if (cancelled) return;
      setPolicy(p);
      setFetched(true);
    });

    return () => {
      cancelled = true;
    };
  }, [enabled]);

  // Derived, not stored: loading is true only while an enabled fetch is in flight.
  const loading = enabled && !fetched;

  return { policy, loading };
}
