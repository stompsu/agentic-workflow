import { useEffect, useState } from 'react';

/** Seconds remaining until `until` (epoch ms); 0 when null/elapsed. Re-evaluated twice a second while active. */
export function useCountdown(until: number | null): number {
  const [, setTick] = useState(0);
  useEffect(() => {
    if (until === null) return;
    const id = setInterval(() => {
      setTick((t) => t + 1);
      if (Date.now() >= until) clearInterval(id);
    }, 500);
    return () => clearInterval(id);
  }, [until]);
  if (until === null) return 0;
  // eslint-disable-next-line react-hooks/purity -- the clock is the input; the interval above re-renders to refresh it
  return Math.max(0, Math.ceil((until - Date.now()) / 1000));
}
