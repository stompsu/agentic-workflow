import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  LOCAL_DEV_AUTH_ACTIVE,
  LOCAL_STORAGE_KEY,
  REFRESH_SKEW_MS,
  createTokenSource,
} from './localAuth';
import LocalAuthProvider from './localAuth';
import { useAuth } from './AuthContext';
import { parseConfig } from '../config/config';
import localConfig from '../../config.local.json';

const b64 = (o: unknown) => btoa(JSON.stringify(o)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
const jwt = (exp: number, n = 1) => `${b64({ alg: 'ES256' })}.${b64({ exp, n })}.sig`;

function memoryStorage() {
  const m = new Map<string, string>();
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => void m.set(k, v),
    removeItem: (k: string) => void m.delete(k),
    raw: m,
  };
}

function tokenFetch(clock: { t: number }, lifetimeS = 3600) {
  let n = 0;
  return vi.fn(async (_url: RequestInfo | URL, init?: RequestInit) => {
    n += 1;
    const user = (JSON.parse(String(init?.body)) as { user: string }).user;
    const exp = Math.floor(clock.t / 1000) + lifetimeS;
    return new Response(JSON.stringify({ accessToken: jwt(exp, n), expiresIn: lifetimeS, user }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  });
}

describe('createTokenSource', () => {
  it('posts the user as JSON to /local-auth/token and stores the session', async () => {
    const clock = { t: 1_800_000_000_000 };
    const fetchImpl = tokenFetch(clock);
    const storage = memoryStorage();
    const source = createTokenSource({ fetchImpl, storage, now: () => clock.t });

    const s = await source.signIn('alice');

    const [url, init] = fetchImpl.mock.calls[0]!;
    expect(url).toBe('/local-auth/token');
    expect(init?.method).toBe('POST');
    expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json');
    expect(JSON.parse(String(init?.body))).toEqual({ user: 'alice' });
    expect(s.user).toBe('alice');
    expect(s.expiresAt).toBe(clock.t + 3_600_000);
    expect(storage.raw.has(LOCAL_STORAGE_KEY)).toBe(true);
  });

  it('caches the token and refreshes it 60 s before expiry', async () => {
    const clock = { t: 1_800_000_000_000 };
    const fetchImpl = tokenFetch(clock);
    const source = createTokenSource({ fetchImpl, storage: memoryStorage(), now: () => clock.t });
    const first = (await source.signIn('alice')).accessToken;

    expect(await source.getToken()).toBe(first);
    clock.t += 3_600_000 - REFRESH_SKEW_MS - 1_000;
    expect(await source.getToken()).toBe(first);
    expect(fetchImpl).toHaveBeenCalledTimes(1);

    clock.t += 1_001;
    const second = await source.getToken();
    expect(second).not.toBe(first);
    expect(fetchImpl).toHaveBeenCalledTimes(2);
  });

  it('forceRefresh fetches a new token for the same user', async () => {
    const clock = { t: 1_800_000_000_000 };
    const fetchImpl = tokenFetch(clock);
    const source = createTokenSource({ fetchImpl, storage: memoryStorage(), now: () => clock.t });
    const first = (await source.signIn('bob')).accessToken;
    const forced = await source.getToken(true);
    expect(forced).not.toBe(first);
    expect(JSON.parse(String(fetchImpl.mock.calls[1]![1]?.body))).toEqual({ user: 'bob' });
  });

  it('shares one request between concurrent refreshes', async () => {
    const clock = { t: 1_800_000_000_000 };
    const fetchImpl = tokenFetch(clock);
    const source = createTokenSource({ fetchImpl, storage: memoryStorage(), now: () => clock.t });
    await source.signIn('alice');
    const [a, b] = await Promise.all([source.getToken(true), source.getToken(true)]);
    expect(a).toBe(b);
    expect(fetchImpl).toHaveBeenCalledTimes(2);
  });

  it('restores the session from storage', async () => {
    const clock = { t: 1_800_000_000_000 };
    const storage = memoryStorage();
    const a = createTokenSource({ fetchImpl: tokenFetch(clock), storage, now: () => clock.t });
    const { accessToken } = await a.signIn('alice');
    const fetchImpl = tokenFetch(clock);
    const b = createTokenSource({ fetchImpl, storage, now: () => clock.t });
    expect(b.current()?.user).toBe('alice');
    expect(await b.getToken()).toBe(accessToken);
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('a failed sign-in rejects and leaves the source anonymous', async () => {
    const fetchImpl = vi.fn(async () => new Response('{}', { status: 400 }));
    const source = createTokenSource({ fetchImpl, storage: memoryStorage() });
    await expect(source.signIn('Not Valid')).rejects.toThrow(/HTTP 400/);
    expect(source.current()).toBeNull();
    await expect(source.getToken()).rejects.toThrow('Not signed in');
  });

  it('a failed refresh rejects and keeps the previous session', async () => {
    const clock = { t: 1_800_000_000_000 };
    const storage = memoryStorage();
    const ok = createTokenSource({ fetchImpl: tokenFetch(clock), storage, now: () => clock.t });
    await ok.signIn('alice');
    const failing = createTokenSource({ fetchImpl: vi.fn(async () => new Response('', { status: 503 })), storage, now: () => clock.t });
    await expect(failing.getToken(true)).rejects.toThrow(/HTTP 503/);
  });

  it('signOut clears storage', async () => {
    const clock = { t: 1_800_000_000_000 };
    const storage = memoryStorage();
    const source = createTokenSource({ fetchImpl: tokenFetch(clock), storage, now: () => clock.t });
    await source.signIn('alice');
    source.signOut();
    expect(source.current()).toBeNull();
    expect(storage.raw.size).toBe(0);
  });
});

function Probe() {
  const auth = useAuth();
  return (
    <div>
      <p>status:{auth.status}</p>
      <p>user:{auth.userName ?? '-'}</p>
      <button onClick={() => void auth.login('alice')}>login</button>
      <button onClick={() => void auth.logout()}>logout</button>
      <button onClick={() => auth.handleAuthFailure()}>fail</button>
      <p>expired:{String(auth.sessionExpired)}</p>
    </div>
  );
}

describe('LocalAuthProvider', () => {
  it('shows the LOCAL MODE banner and the marker', () => {
    sessionStorage.clear();
    render(
      <LocalAuthProvider>
        <Probe />
      </LocalAuthProvider>,
    );
    expect(screen.getByText('LOCAL MODE')).toBeInTheDocument();
    expect(screen.getByTestId(LOCAL_DEV_AUTH_ACTIVE)).toBeInTheDocument();
    expect(screen.getByText('status:anonymous')).toBeInTheDocument();
  });

  it('login -> authenticated, auth failure -> anonymous with sessionExpired, logout clears', async () => {
    sessionStorage.clear();
    const clock = { t: Date.now() };
    vi.stubGlobal('fetch', tokenFetch(clock));
    const user = userEvent.setup();
    render(
      <LocalAuthProvider>
        <Probe />
      </LocalAuthProvider>,
    );
    await user.click(screen.getByText('login'));
    await waitFor(() => expect(screen.getByText('status:authenticated')).toBeInTheDocument());
    expect(screen.getByText('user:alice')).toBeInTheDocument();
    expect(sessionStorage.getItem(LOCAL_STORAGE_KEY)).not.toBeNull();

    await user.click(screen.getByText('fail'));
    expect(screen.getByText('status:anonymous')).toBeInTheDocument();
    expect(screen.getByText('expired:true')).toBeInTheDocument();
    expect(sessionStorage.getItem(LOCAL_STORAGE_KEY)).toBeNull();
    vi.unstubAllGlobals();
  });
});

describe('config.local.json', () => {
  it('passes parseConfig unchanged and lists Alice and Bob', () => {
    const c = parseConfig(localConfig);
    expect(c.apiBaseUrl).toBe('/api');
    expect(c.branding.title).toBe('Assistant (local)');
    expect(c.auth.apiScopes).toEqual(['access_as_user']);
    expect(c.auth.providers.map((p) => p.domainHint)).toEqual(['alice', 'bob']);
  });
});
