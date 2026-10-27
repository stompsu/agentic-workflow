import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render } from '@testing-library/react';
import { SocialAuthContext, useAuth, type AuthContextValue, type SocialAuthValue } from './AuthContext';
import { GuestLayer } from './GuestLayer';
import { GUEST_STORAGE_KEY } from './guestSession';
import { json, problem } from '../test/helpers';

/** Same-origin BroadcastChannel stand-in: delivers to every other open channel of the same name, synchronously. */
class MockChannel {
  static all: MockChannel[] = [];
  static sent: unknown[] = [];
  onmessage: ((e: MessageEvent<unknown>) => void) | null = null;
  closed = false;
  constructor(readonly name: string) {
    MockChannel.all.push(this);
  }
  postMessage(data: unknown) {
    MockChannel.sent.push(data);
    for (const c of MockChannel.all) if (c !== this && !c.closed && c.name === this.name) c.onmessage?.({ data } as MessageEvent<unknown>);
  }
  close() {
    this.closed = true;
  }
}

const NOW = Date.parse('2026-10-27T10:00:00.000Z');
const iso = (offsetMs: number) => new Date(NOW + offsetMs).toISOString();
const HOUR = 3_600_000;

function social(over: Partial<SocialAuthValue> = {}): SocialAuthValue {
  return {
    status: 'anonymous',
    userName: null,
    sessionExpired: false,
    login: vi.fn(async () => undefined),
    logout: vi.fn(async () => undefined),
    getToken: vi.fn(async () => 'social-token'),
    handleAuthFailure: vi.fn(),
    ...over,
  };
}

function store(accessToken: string, expiresIn: number) {
  sessionStorage.setItem(GUEST_STORAGE_KEY, JSON.stringify({ accessToken, expiresAt: iso(expiresIn) }));
}

interface Harness {
  auth: () => AuthContextValue;
}

function mount(s: SocialAuthValue, props: { guestEnabled?: boolean } = {}): Harness & { unmount: () => void } {
  let latest: AuthContextValue | null = null;
  function Probe() {
    latest = useAuth();
    return <p>{`${latest.status}:${latest.mode}`}</p>;
  }
  const r = render(
    <SocialAuthContext.Provider value={s}>
      <GuestLayer guestEnabled={props.guestEnabled ?? true} apiBaseUrl="/api">
        <Probe />
      </GuestLayer>
    </SocialAuthContext.Provider>,
  );
  return { auth: () => latest!, unmount: r.unmount };
}

beforeEach(() => {
  vi.useFakeTimers({ now: NOW });
  sessionStorage.clear();
  MockChannel.all = [];
  MockChannel.sent = [];
  vi.stubGlobal('BroadcastChannel', MockChannel);
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('GuestLayer start-up (ADR 0006 section 4)', () => {
  it('uses a stored guest token with more than 60 s left: guest mode, token is the bearer, no name', () => {
    store('g-token', 4 * HOUR);
    const h = mount(social());
    expect(h.auth()).toMatchObject({ status: 'authenticated', mode: 'guest', userName: null });
  });

  it('getToken returns the guest token and never calls the social token source', async () => {
    store('g-token', 4 * HOUR);
    const s = social();
    const h = mount(s);
    await expect(h.auth().getToken(true)).resolves.toBe('g-token');
    expect(s.getToken).not.toHaveBeenCalled();
  });

  it('a token with 60 s or less left is dropped: login page, storage cleared', () => {
    store('g-token', 60_000);
    const h = mount(social());
    expect(h.auth()).toMatchObject({ status: 'anonymous', mode: 'social' });
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
  });

  it('an MSAL account wins: social mode, stale guest token removed, nothing broadcast', () => {
    store('stale', 4 * HOUR);
    const h = mount(social({ status: 'authenticated', userName: 'Ada' }));
    expect(h.auth()).toMatchObject({ status: 'authenticated', mode: 'social', userName: 'Ada' });
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
    expect(MockChannel.sent).toEqual([]);
  });

  it('stays loading while the social layer is loading', () => {
    expect(mount(social({ status: 'loading' })).auth().status).toBe('loading');
  });

  it('guest disabled in config: stored token ignored and removed, no channel is opened', () => {
    store('g-token', 4 * HOUR);
    const h = mount(social(), { guestEnabled: false });
    expect(h.auth().status).toBe('anonymous');
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
    expect(MockChannel.all).toHaveLength(0);
  });
});

describe('GuestLayer.loginAsGuest', () => {
  it('POSTs {} without Authorization, stores the token in sessionStorage and enters guest mode', async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () => json({ accessToken: 'new-token', tokenType: 'Bearer', expiresAt: iso(4 * HOUR) }));
    vi.stubGlobal('fetch', fetchMock);
    const h = mount(social());
    await act(async () => {
      await h.auth().loginAsGuest();
    });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/guest/token');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe('{}');
    expect(new Headers(init?.headers).has('Authorization')).toBe(false);
    expect(h.auth()).toMatchObject({ status: 'authenticated', mode: 'guest' });
    expect(JSON.parse(sessionStorage.getItem(GUEST_STORAGE_KEY)!)).toEqual({ accessToken: 'new-token', expiresAt: iso(4 * HOUR) });
    expect(localStorage.length).toBe(0);
    await expect(h.auth().getToken()).resolves.toBe('new-token');
  });

  it('propagates 429 rate_limited with Retry-After and 404 guest_access_disabled as ApiError; stays anonymous', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => problem(429, 'rate_limited', { 'Retry-After': '42' })));
    const h = mount(social());
    await act(async () => {
      await expect(h.auth().loginAsGuest()).rejects.toMatchObject({ status: 429, code: 'rate_limited', retryAfterSeconds: 42 });
    });
    vi.stubGlobal('fetch', vi.fn(async () => problem(404, 'guest_access_disabled')));
    await act(async () => {
      await expect(h.auth().loginAsGuest()).rejects.toMatchObject({ code: 'guest_access_disabled' });
    });
    expect(h.auth().status).toBe('anonymous');
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
  });
});

describe('GuestLayer expiry and logout', () => {
  it('the timer at expiresAt ends guest mode: login page, storage cleared, notice, guest-ended broadcast', () => {
    store('g-token', 2 * HOUR);
    const h = mount(social());
    act(() => void vi.advanceTimersByTime(2 * HOUR - 1));
    expect(h.auth().mode).toBe('guest');
    act(() => void vi.advanceTimersByTime(2));
    expect(h.auth()).toMatchObject({ status: 'anonymous', guestNotice: 'expired' });
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
    expect(MockChannel.sent).toContainEqual({ type: 'guest-ended' });
  });

  it('logout in guest mode clears storage and broadcasts guest-ended without calling the social logout', async () => {
    store('g-token', 2 * HOUR);
    const s = social();
    const h = mount(s);
    await act(async () => h.auth().logout());
    expect(h.auth().status).toBe('anonymous');
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
    expect(MockChannel.sent).toContainEqual({ type: 'guest-ended' });
    expect(s.logout).not.toHaveBeenCalled();
  });

  it('logout in social mode also clears guest storage, broadcasts and calls the social logout', async () => {
    const s = social({ status: 'authenticated', userName: 'Ada' });
    const h = mount(s);
    sessionStorage.setItem(GUEST_STORAGE_KEY, 'leftover');
    await act(async () => h.auth().logout());
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
    expect(MockChannel.sent).toContainEqual({ type: 'guest-ended' });
    expect(s.logout).toHaveBeenCalledTimes(1);
  });

  it('endGuest(deleted | delete-failed | silent) sets the matching notice; handleGuestEnded / handleAuthFailure end the guest', () => {
    store('g-token', 2 * HOUR);
    const s = social();
    const h = mount(s);
    act(() => h.auth().endGuest('deleted'));
    expect(h.auth().guestNotice).toBe('deleted');
    store('g2', 2 * HOUR);
    const h2 = mount(s);
    act(() => h2.auth().endGuest('delete-failed'));
    expect(h2.auth().guestNotice).toBe('delete-failed');
    store('g3', 2 * HOUR);
    const h3 = mount(s);
    act(() => h3.auth().handleGuestEnded());
    expect(h3.auth()).toMatchObject({ status: 'anonymous', guestNotice: 'expired' });
    store('g4', 2 * HOUR);
    const h4 = mount(s);
    act(() => h4.auth().handleAuthFailure());
    expect(h4.auth()).toMatchObject({ status: 'anonymous', guestNotice: 'expired' });
    expect(s.handleAuthFailure).not.toHaveBeenCalled();
  });

  it('handleAuthFailure in social mode is delegated to the social layer', () => {
    const s = social({ status: 'authenticated' });
    mount(s).auth().handleAuthFailure();
    expect(s.handleAuthFailure).toHaveBeenCalledTimes(1);
  });
});

describe('GuestLayer tab hand-off (BroadcastChannel)', () => {
  /** Tab A holds the guest; tab B starts empty. sessionStorage is shared in jsdom, so B starts after A and the key is cleared. */
  function tabs(aExpiresIn = 4 * HOUR) {
    store('shared-token', aExpiresIn);
    const a = mount(social());
    sessionStorage.clear();
    return a;
  }

  it('a tab without a token shows the login page at once (no blocking) and takes over a token within 300 ms', () => {
    tabs();
    const b = mount(social());
    // Synchronous mock: the reply already arrived, but the status never was 'loading'.
    expect(b.auth()).toMatchObject({ status: 'authenticated', mode: 'guest' });
    expect(MockChannel.sent[0]).toEqual({ type: 'guest-token-request' });
    expect(JSON.parse(sessionStorage.getItem(GUEST_STORAGE_KEY)!).accessToken).toBe('shared-token');
  });

  it('does not block the login page when nobody answers', () => {
    const b = mount(social());
    expect(b.auth().status).toBe('anonymous');
    expect(MockChannel.sent).toEqual([{ type: 'guest-token-request' }]);
  });

  it('a reply after the 300 ms window is ignored', () => {
    const b = mount(social());
    act(() => void vi.advanceTimersByTime(301));
    const other = new MockChannel('supportchat-guest');
    act(() => other.postMessage({ type: 'guest-token', accessToken: 'late', expiresAt: iso(4 * HOUR) }));
    expect(b.auth().status).toBe('anonymous');
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
  });

  it('a tab only hands over a token with more than 60 s left', () => {
    const a = tabs(4 * HOUR);
    act(() => void vi.advanceTimersByTime(4 * HOUR - 30_000)); // 30 s left in A
    expect(a.auth().mode).toBe('guest');
    const b = mount(social());
    expect(b.auth().status).toBe('anonymous');
    expect(MockChannel.sent.some((m) => (m as { type: string }).type === 'guest-token')).toBe(false);
  });

  it('a received token with 60 s or less left is not accepted', () => {
    const b = mount(social());
    const other = new MockChannel('supportchat-guest');
    act(() => other.postMessage({ type: 'guest-token', accessToken: 'dying', expiresAt: iso(60_000) }));
    expect(b.auth().status).toBe('anonymous');
  });

  it('a tab in social mode never accepts a guest token', () => {
    const b = mount(social({ status: 'authenticated' }));
    const other = new MockChannel('supportchat-guest');
    act(() => other.postMessage({ type: 'guest-token', accessToken: 'x', expiresAt: iso(4 * HOUR) }));
    expect(b.auth().mode).toBe('social');
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
  });

  it('guest-ended from another tab clears storage and sends a guest-mode tab to the login page with a notice', () => {
    store('g-token', 2 * HOUR);
    const h = mount(social());
    const other = new MockChannel('supportchat-guest');
    act(() => other.postMessage({ type: 'guest-ended' }));
    expect(h.auth()).toMatchObject({ status: 'anonymous', guestNotice: 'expired' });
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
  });

  it('guest-ended in a tab that was not a guest only clears storage (no notice)', () => {
    const h = mount(social());
    sessionStorage.setItem(GUEST_STORAGE_KEY, 'x');
    const other = new MockChannel('supportchat-guest');
    act(() => other.postMessage({ type: 'guest-ended' }));
    expect(h.auth().guestNotice).toBeNull();
    expect(sessionStorage.getItem(GUEST_STORAGE_KEY)).toBeNull();
  });

  it('ignores malformed messages', () => {
    const h = mount(social());
    const other = new MockChannel('supportchat-guest');
    act(() => {
      other.postMessage(null);
      other.postMessage({ type: 'guest-token', accessToken: 5 });
      other.postMessage('x');
    });
    expect(h.auth().status).toBe('anonymous');
  });
});
