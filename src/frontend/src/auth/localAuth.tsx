// LOCAL-ONLY sign-in (ADR 0005 section 5). Imported solely behind the compile-time constant __LOCAL_AUTH__ (true only
// for `vite --mode localdev` serve, never for `vite build`). scripts/check-dist.mjs fails the build if the marker
// below ends up in dist/. It talks to the LocalHost token endpoint through the Vite proxy (same origin, so the
// endpoint's Origin allow-list http://localhost:5173 matches); the config's `authority` is a placeholder and ignored.
import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { AuthContext, type AuthContextValue } from './AuthContext';

export const LOCAL_DEV_AUTH_ACTIVE = 'LOCAL_DEV_AUTH_ACTIVE';
export const LOCAL_TOKEN_URL = '/local-auth/token';
export const LOCAL_STORAGE_KEY = 'supportchat.localAuth';
/** A token is replaced this long before it expires. */
export const REFRESH_SKEW_MS = 60_000;

export interface LocalSession {
  user: string;
  accessToken: string;
  /** Epoch milliseconds. */
  expiresAt: number;
}

export interface TokenSourceOptions {
  fetchImpl?: typeof fetch;
  storage?: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
  now?: () => number;
  url?: string;
}

export interface TokenSource {
  current(): LocalSession | null;
  signIn(user: string): Promise<LocalSession>;
  /** Cached token; fetched again when `forceRefresh` or when it expires within REFRESH_SKEW_MS. */
  getToken(forceRefresh?: boolean): Promise<string>;
  signOut(): void;
}

function jwtExpiry(token: string): number | null {
  try {
    const part = token.split('.')[1];
    if (!part) return null;
    const json = atob(part.replace(/-/g, '+').replace(/_/g, '/'));
    const exp = (JSON.parse(json) as { exp?: unknown }).exp;
    return typeof exp === 'number' ? exp * 1000 : null;
  } catch {
    return null;
  }
}

function readStored(storage: TokenSourceOptions['storage']): LocalSession | null {
  try {
    const raw = storage?.getItem(LOCAL_STORAGE_KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as Partial<LocalSession>;
    if (typeof s.user === 'string' && typeof s.accessToken === 'string' && typeof s.expiresAt === 'number') {
      return { user: s.user, accessToken: s.accessToken, expiresAt: s.expiresAt };
    }
  } catch {
    // fall through: treat a corrupt entry as signed out
  }
  return null;
}

export function createTokenSource(options: TokenSourceOptions = {}): TokenSource {
  const fetchImpl = options.fetchImpl ?? ((...args: Parameters<typeof fetch>) => fetch(...args));
  const storage = options.storage ?? (typeof sessionStorage === 'undefined' ? undefined : sessionStorage);
  const now = options.now ?? Date.now;
  const url = options.url ?? LOCAL_TOKEN_URL;

  let session = readStored(storage);
  let inflight: { user: string; promise: Promise<LocalSession> } | null = null;

  const store = (s: LocalSession | null) => {
    session = s;
    try {
      if (s) storage?.setItem(LOCAL_STORAGE_KEY, JSON.stringify(s));
      else storage?.removeItem(LOCAL_STORAGE_KEY);
    } catch {
      // storage unavailable: keep the in-memory session
    }
  };

  async function request(user: string): Promise<LocalSession> {
    const response = await fetchImpl(url, {
      method: 'POST',
      cache: 'no-store',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ user }),
    });
    if (!response.ok) throw new Error(`Local sign-in failed (HTTP ${response.status})`);
    const body = (await response.json()) as { accessToken?: unknown; expiresIn?: unknown; user?: unknown };
    if (typeof body.accessToken !== 'string' || body.accessToken === '') throw new Error('Local sign-in returned no token');
    const fromExpiresIn = typeof body.expiresIn === 'number' ? now() + body.expiresIn * 1000 : null;
    const expiresAt = jwtExpiry(body.accessToken) ?? fromExpiresIn;
    if (expiresAt === null) throw new Error('Local sign-in returned no expiry');
    return { user: typeof body.user === 'string' && body.user !== '' ? body.user : user, accessToken: body.accessToken, expiresAt };
  }

  function fetchFor(user: string): Promise<LocalSession> {
    if (inflight?.user === user) return inflight.promise;
    const promise = request(user)
      .then((s) => {
        store(s);
        return s;
      })
      .finally(() => {
        if (inflight?.promise === promise) inflight = null;
      });
    inflight = { user, promise };
    return promise;
  }

  return {
    current: () => session,
    signIn: (user) => fetchFor(user),
    async getToken(forceRefresh = false) {
      if (!session) throw new Error('Not signed in');
      if (!forceRefresh && now() < session.expiresAt - REFRESH_SKEW_MS) return session.accessToken;
      return (await fetchFor(session.user)).accessToken;
    },
    signOut: () => store(null),
  };
}

export default function LocalAuthProvider({ children }: { children: ReactNode }) {
  const [source] = useState<TokenSource>(() => createTokenSource());

  const [user, setUser] = useState<string | null>(() => source.current()?.user ?? null);
  const [expired, setExpired] = useState(false);

  const getToken = useCallback((forceRefresh = false) => source.getToken(forceRefresh), [source]);
  const handleAuthFailure = useCallback(() => {
    source.signOut();
    setUser(null);
    setExpired(true);
  }, [source]);

  const value = useMemo<AuthContextValue>(
    () => ({
      status: user ? 'authenticated' : 'anonymous',
      userName: user,
      sessionExpired: expired,
      login: async (domainHint) => {
        if (!domainHint) throw new Error('A local user is required');
        setExpired(false);
        const s = await source.signIn(domainHint);
        setUser(s.user);
      },
      logout: async () => {
        source.signOut();
        setUser(null);
        setExpired(false);
      },
      getToken,
      handleAuthFailure,
    }),
    [user, expired, source, getToken, handleAuthFailure],
  );

  return (
    <AuthContext.Provider value={value}>
      <div className="banner banner-info local-banner" data-testid={LOCAL_DEV_AUTH_ACTIVE}>
        <span>
          <strong>LOCAL MODE</strong>: signed in with a local test identity, chats live in memory only and are lost when the
          local backend restarts.
        </span>
      </div>
      {children}
    </AuthContext.Provider>
  );
}
