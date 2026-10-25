import { useEffect, useState } from 'react';

/** Seconds remaining until `until` (epoch ms); 0 when null/elapsed. Re-evaluated twice a second while active. */
export function useCountdown(until: number | null): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (until === null) return;
    const id = setInterval(() => {
      const n = Date.now();
      setNow(n);
      if (n >= until) clearInterval(id);
    }, 500);
    return () => clearInterval(id);
  }, [until]);
  if (until === null) return 0;
  return Math.max(0, Math.ceil((until - now) / 1000));
}
