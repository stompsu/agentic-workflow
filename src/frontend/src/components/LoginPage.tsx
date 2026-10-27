import { useState } from 'react';
import type { GuestConfig, ProviderConfig } from '../config/config';
import {
  DEFAULT_BRANDING,
  DEFAULT_GUEST_RETENTION_HOURS,
  DEFAULT_GUEST_SESSION_HOURS,
  type AppInfoConfig,
  type BrandingConfig,
} from '../config/config';
import { useAuth, type GuestNotice } from '../auth/AuthContext';
import { ApiError, describeApiError } from '../api/errors';
import { useCountdown } from '../chat/useCountdown';

interface Props {
  providers: ProviderConfig[];
  branding?: BrandingConfig;
  /** The third button is rendered only when `guest.enabled`. */
  guest?: GuestConfig;
  info?: AppInfoConfig;
}

function noticeText(notice: GuestNotice, retentionHours: number): string {
  switch (notice) {
    case 'deleted':
      return 'Your guest session has ended and its chats were deleted.';
    case 'delete-failed':
      return `Your guest session has ended. Remaining chats will be deleted automatically within ${retentionHours} hours.`;
    default:
      return 'Your guest session has ended.';
  }
}

export function LoginPage({ providers, branding = DEFAULT_BRANDING, guest, info = {} }: Props) {
  const { login, loginAsGuest, sessionExpired, guestNotice } = useAuth();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [blockedUntil, setBlockedUntil] = useState<number | null>(null);
  const blockedSeconds = useCountdown(blockedUntil);

  const sessionHours = info.guestSessionHours ?? DEFAULT_GUEST_SESSION_HOURS;
  const retentionHours = info.guestRetentionHours ?? DEFAULT_GUEST_RETENTION_HOURS;

  async function start(domainHint?: string) {
    setError(null);
    setPending(true);
    try {
      await login(domainHint);
    } catch {
      setError('Sign-in could not be started. Please try again.');
      setPending(false);
    }
  }

  async function startGuest() {
    setError(null);
    setPending(true);
    try {
      await loginAsGuest();
      // Guest mode replaces this page.
    } catch (e) {
      if (e instanceof ApiError && e.code === 'rate_limited' && e.retryAfterSeconds !== undefined) {
        setBlockedUntil(Date.now() + e.retryAfterSeconds * 1000);
        setError('Too many guest sessions are being started right now.');
      } else {
        setError(e instanceof ApiError ? describeApiError(e) : 'Guest sign-in could not be started. Please try again.');
      }
      setPending(false);
    }
  }

  return (
    <main className="login">
      <h1>{branding.title}</h1>
      <p>{branding.tagline}</p>
      {sessionExpired && (
        <p role="alert" className="banner banner-info">
          Your session has expired. Please sign in again.
        </p>
      )}
      {guestNotice && (
        <p role="status" className="banner banner-info">
          {noticeText(guestNotice, retentionHours)}
        </p>
      )}
      {error && (
        <p role="alert" className="banner banner-error">
          {error}
          {blockedSeconds > 0 && ` Try again in ${blockedSeconds} seconds.`}
        </p>
      )}
      <div className="provider-buttons">
        {providers.length > 0 ? (
          providers.map((p) => (
            <button key={p.id} type="button" className="btn btn-provider" disabled={pending} onClick={() => void start(p.domainHint)}>
              {p.label}
            </button>
          ))
        ) : (
          <button type="button" className="btn btn-provider" disabled={pending} onClick={() => void start()}>
            Sign in
          </button>
        )}
      </div>
      {guest?.enabled && (
        <div className="guest-option">
          <p className="divider" aria-hidden="true">
            <span>or</span>
          </p>
          <button
            type="button"
            className="btn btn-provider btn-secondary btn-block"
            disabled={pending || blockedSeconds > 0}
            aria-describedby="guest-caption"
            onClick={() => void startGuest()}
          >
            {guest.label}
          </button>
          <p id="guest-caption" className="hint">
            No account needed. Your chats stay only in this browser for up to {sessionHours} hours and are deleted automatically within{' '}
            {retentionHours} hours after your last message.
          </p>
        </div>
      )}
    </main>
  );
}
