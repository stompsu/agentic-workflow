import { createContext, useContext } from 'react';

export interface AuthContextValue {
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

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside an AuthProvider');
  return ctx;
}
