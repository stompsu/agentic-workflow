import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { AuthContext, useSocialAuth, type AuthContextValue, type GuestNotice } from './AuthContext';
import {
  HANDOFF_WINDOW_MS,
  MIN_REMAINING_MS,
  clearGuestToken,
  openGuestChannel,
  parseGuestMessage,
  parseGuestToken,
  readGuestToken,
  remainingMs,
  requestGuestToken,
  writeGuestToken,
  type GuestMessage,
  type GuestToken,
} from './guestSession';

const MAX_TIMEOUT_MS = 2 ** 31 - 1;

interface Props {
  /** `auth.guest.enabled` from /config.json. When false nothing guest-related is read, stored or sent. */
  guestEnabled: boolean;
  apiBaseUrl: string;
  children: ReactNode;
}

/**
 * Guest mode on top of the social identity (ADR 0006 sections 2 and 4). Start-up order:
 *  1. social account present: social mode; any stale guest token is removed (no broadcast);
 *  2. else this tab holds a guest token with > 60 s left: guest mode;
 *  3. else: login page immediately, and a background hand-off request to other tabs (answer accepted for 300 ms).
 * The guest token lives in sessionStorage only; there is no refresh; the end of the token is the end of the guest.
 */
export function GuestLayer({ guestEnabled, apiBaseUrl, children }: Props) {
  const social = useSocialAuth();
  const socialAuthed = social.status === 'authenticated';
  const socialSettled = social.status !== 'loading';

  const [guest, setGuest] = useState<GuestToken | null>(() => {
    if (!guestEnabled) {
      clearGuestToken();
      return null;
    }
    const stored = readGuestToken();
    if (stored && remainingMs(stored) > MIN_REMAINING_MS) return stored;
    if (stored) clearGuestToken();
    return null;
  });
  const [notice, setNotice] = useState<GuestNotice | null>(null);

  const activeGuest = socialAuthed ? null : guest;

  const channelRef = useRef<BroadcastChannel | null>(null);
  const awaitingRef = useRef(false);
  const handoffDoneRef = useRef(false);
  const activeGuestRef = useRef<GuestToken | null>(activeGuest);
  const socialAuthedRef = useRef(socialAuthed);
  useEffect(() => {
    activeGuestRef.current = activeGuest;
    socialAuthedRef.current = socialAuthed;
  });

  // 1. A social account wins: the guest token in this tab is stale (state dropped during render, storage in the effect).
  if (socialAuthed && guest !== null) setGuest(null);
  useEffect(() => {
    if (socialAuthed) clearGuestToken();
  }, [socialAuthed]);

  const broadcastEnded = useCallback(() => {
    channelRef.current?.postMessage({ type: 'guest-ended' } satisfies GuestMessage);
  }, []);

  const endGuest = useCallback(
    (outcome: GuestNotice | 'silent') => {
      clearGuestToken();
      setGuest(null);
      setNotice(outcome === 'silent' ? null : outcome);
      broadcastEnded();
    },
    [broadcastEnded],
  );

  // Tab hand-off channel.
  useEffect(() => {
    if (!guestEnabled) return;
    const channel = openGuestChannel();
    channelRef.current = channel;
    if (!channel) return;
    channel.onmessage = (event: MessageEvent<unknown>) => {
      const msg = parseGuestMessage(event.data);
      if (!msg) return;
      const current = activeGuestRef.current;
      if (msg.type === 'guest-token-request') {
        if (current && remainingMs(current) > MIN_REMAINING_MS) {
          channel.postMessage({
            type: 'guest-token',
            accessToken: current.accessToken,
            expiresAt: new Date(current.expiresAt).toISOString(),
          } satisfies GuestMessage);
        }
      } else if (msg.type === 'guest-token') {
        if (!awaitingRef.current || current || socialAuthedRef.current) return;
        const token = parseGuestToken(msg.accessToken, msg.expiresAt);
        if (!token || remainingMs(token) <= MIN_REMAINING_MS) return;
        awaitingRef.current = false;
        writeGuestToken(token);
        setGuest(token);
      } else {
        clearGuestToken();
        setGuest(null);
        if (current) setNotice('expired');
      }
    };
    return () => {
      channel.close();
      channelRef.current = null;
    };
  }, [guestEnabled]);

  // 3. No account and no token here: ask other tabs without blocking the login page.
  const hasGuest = activeGuest !== null;
  useEffect(() => {
    if (!guestEnabled || !socialSettled || socialAuthed || hasGuest || handoffDoneRef.current) return;
    const channel = channelRef.current;
    if (!channel) return;
    awaitingRef.current = true;
    channel.postMessage({ type: 'guest-token-request' } satisfies GuestMessage);
    const timer = setTimeout(() => {
      awaitingRef.current = false;
      handoffDoneRef.current = true;
    }, HANDOFF_WINDOW_MS);
    return () => {
      clearTimeout(timer);
      awaitingRef.current = false;
    };
  }, [guestEnabled, socialSettled, socialAuthed, hasGuest]);

  // The guest ends at the token's expiry. No renewal.
  const expiresAt = activeGuest?.expiresAt;
  useEffect(() => {
    if (expiresAt === undefined) return;
    const timer = setTimeout(() => endGuest('expired'), Math.min(Math.max(expiresAt - Date.now(), 0), MAX_TIMEOUT_MS));
    return () => clearTimeout(timer);
  }, [expiresAt, endGuest]);

  const socialGetToken = social.getToken;
  const getToken = useCallback(
    async (forceRefresh?: boolean): Promise<string> => (activeGuest ? activeGuest.accessToken : socialGetToken(forceRefresh)),
    [activeGuest, socialGetToken],
  );

  const loginAsGuest = useCallback(async () => {
    if (!guestEnabled) throw new Error('Guest access is disabled');
    const token = await requestGuestToken(apiBaseUrl);
    writeGuestToken(token);
    setNotice(null);
    setGuest(token);
  }, [guestEnabled, apiBaseUrl]);

  const socialLogin = social.login;
  const socialLogout = social.logout;
  const socialFailure = social.handleAuthFailure;
  const isGuestActive = activeGuest !== null;

  const value = useMemo<AuthContextValue>(
    () => ({
      status: !socialSettled ? 'loading' : socialAuthed || isGuestActive ? 'authenticated' : social.status,
      mode: isGuestActive ? 'guest' : 'social',
      userName: isGuestActive ? null : social.userName,
      sessionExpired: social.sessionExpired,
      guestNotice: notice,
      login: async (domainHint) => {
        setNotice(null);
        await socialLogin(domainHint);
      },
      loginAsGuest,
      endGuest,
      logout: async () => {
        // Logout in either mode clears guest storage and tells the other tabs.
        endGuest('silent');
        if (socialAuthed) await socialLogout();
      },
      getToken,
      handleAuthFailure: () => (isGuestActive ? endGuest('expired') : socialFailure()),
      handleGuestEnded: () => endGuest('expired'),
    }),
    [socialSettled, socialAuthed, isGuestActive, social.status, social.userName, social.sessionExpired, notice, loginAsGuest, endGuest, getToken, socialLogin, socialLogout, socialFailure],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
