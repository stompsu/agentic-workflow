import { createContext, useContext } from 'react';

/** Why the login page shows a guest notice (ADR 0006 section 2). */
export type GuestNotice = 'expired' | 'deleted' | 'delete-failed';

/** What the identity provider layer (MSAL or the e2e mock) provides. Guest mode is layered on top (GuestLayer). */
export interface SocialAuthValue {
  status: 'loading' | 'anonymous' | 'authenticated';
  /** Display name for the header (plain text). */
  userName: string | null;
  /** True when the user was sent back to login because the API rejected the token (401 after refresh). */
  sessionExpired: boolean;
  /** Starts the sign-in redirect. `domainHint` selects the social provider (Entra External ID issuer acceleration). */
  login: (domainHint?: string) => Promise<void>;
  logout: () => Promise<void>;
  /** Access token for the API scope. Silent first; falls back to a redirect when interaction is required. */
  getToken: (forceRefresh?: boolean) => Promise<string>;
  /** Called by the API client on a 401 that survived a forced refresh. */
  handleAuthFailure: () => void;
}

export interface AuthContextValue extends SocialAuthValue {
  /** Which kind of identity is (or would be) active. 'guest' = an anonymous guest token (no account). */
  mode: 'social' | 'guest';
  /** Creates a new guest (POST /api/guest/token) and switches to guest mode. Throws ApiError. */
  loginAsGuest: () => Promise<void>;
  /**
   * Ends guest mode locally: clears the token, tells the other tabs and shows the login page. `outcome` selects the
   * notice: 'deleted' / 'delete-failed' (after End guest session) or 'silent' (no notice, e.g. before signing in).
   */
  endGuest: (outcome: GuestNotice | 'silent') => void;
  /** Notice for the login page after a guest session ended; null otherwise. */
  guestNotice: GuestNotice | null;
  /** Called by the API client on 401 `guest_expired`. */
  handleGuestEnded: () => void;
}

export const SocialAuthContext = createContext<SocialAuthValue | null>(null);
export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside an AuthProvider');
  return ctx;
}

export function useSocialAuth(): SocialAuthValue {
  const ctx = useContext(SocialAuthContext);
  if (!ctx) throw new Error('useSocialAuth must be used inside an identity provider');
  return ctx;
}
