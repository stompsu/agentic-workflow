import { ApiError, errorFromResponse } from '../api/errors';

/** sessionStorage key for the guest token + expiry (ADR 0006 section 4). Never localStorage. */
export const GUEST_STORAGE_KEY = 'agentwf.guest';
export const GUEST_CHANNEL_NAME = 'agentwf-guest';
/** A token is only used at start-up or handed to another tab when it has more than this left. */
export const MIN_REMAINING_MS = 60_000;
/** How long a tab without a token waits for a hand-off reply from another tab. */
export const HANDOFF_WINDOW_MS = 300;

export interface GuestToken {
  accessToken: string;
  /** Epoch ms. */
  expiresAt: number;
}

export type GuestMessage =
  | { type: 'guest-token-request' }
  | { type: 'guest-token'; accessToken: string; expiresAt: string }
  | { type: 'guest-ended' };

export function remainingMs(token: GuestToken, now = Date.now()): number {
  return token.expiresAt - now;
}

export function parseGuestToken(accessToken: unknown, expiresAt: unknown): GuestToken | null {
  if (typeof accessToken !== 'string' || accessToken === '' || typeof expiresAt !== 'string') return null;
  const ms = Date.parse(expiresAt);
  return Number.isNaN(ms) ? null : { accessToken, expiresAt: ms };
}

export function readGuestToken(storage: Storage = sessionStorage): GuestToken | null {
  try {
    const raw = storage.getItem(GUEST_STORAGE_KEY);
    if (!raw) return null;
    const v = JSON.parse(raw) as { accessToken?: unknown; expiresAt?: unknown };
    return parseGuestToken(v.accessToken, v.expiresAt);
  } catch {
    return null;
  }
}

export function writeGuestToken(token: GuestToken, storage: Storage = sessionStorage): void {
  try {
    storage.setItem(GUEST_STORAGE_KEY, JSON.stringify({ accessToken: token.accessToken, expiresAt: new Date(token.expiresAt).toISOString() }));
  } catch {
    /* storage unavailable: the guest still works in this tab until reload */
  }
}

export function clearGuestToken(storage: Storage = sessionStorage): void {
  try {
    storage.removeItem(GUEST_STORAGE_KEY);
  } catch {
    /* ignore */
  }
}

/** POST /api/guest/token (api-contract 2.9): no Authorization header, body `{}`. Throws ApiError. */
export async function requestGuestToken(apiBaseUrl: string, fetchImpl: typeof fetch = (...a) => fetch(...a)): Promise<GuestToken> {
  let response: Response;
  try {
    response = await fetchImpl(`${apiBaseUrl}/guest/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: '{}',
      cache: 'no-store',
    });
  } catch {
    throw new ApiError({ status: 0, code: 'network_error' });
  }
  if (!response.ok) throw await errorFromResponse(response);
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    throw new ApiError({ status: response.status, code: 'internal_error' });
  }
  const b = (typeof body === 'object' && body !== null ? body : {}) as Record<string, unknown>;
  const token = parseGuestToken(b.accessToken, b.expiresAt);
  if (!token || remainingMs(token) <= 0) throw new ApiError({ status: response.status, code: 'internal_error' });
  return token;
}

export function openGuestChannel(): BroadcastChannel | null {
  return typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(GUEST_CHANNEL_NAME);
}

export function parseGuestMessage(data: unknown): GuestMessage | null {
  if (typeof data !== 'object' || data === null) return null;
  const d = data as Record<string, unknown>;
  if (d.type === 'guest-token-request' || d.type === 'guest-ended') return { type: d.type };
  if (d.type === 'guest-token' && typeof d.accessToken === 'string' && typeof d.expiresAt === 'string') {
    return { type: 'guest-token', accessToken: d.accessToken, expiresAt: d.expiresAt };
  }
  return null;
}
