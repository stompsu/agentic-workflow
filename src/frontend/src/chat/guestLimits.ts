import { createContext, useContext } from 'react';

/** Per-guest quotas that were reported as used up by the server (429 `guest_quota_exceeded`, api-contract 2.9). */
export interface GuestLimits {
  /** POST /api/sessions was refused: "New chat" is disabled. */
  sessionsExhausted: boolean;
  /** POST messages was refused: the composer is disabled. */
  turnsExhausted: boolean;
  report: (kind: 'sessions' | 'turns') => void;
}

export const GuestLimitsContext = createContext<GuestLimits>({ sessionsExhausted: false, turnsExhausted: false, report: () => undefined });

export function useGuestLimits(): GuestLimits {
  return useContext(GuestLimitsContext);
}
