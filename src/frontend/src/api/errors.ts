/** Stable machine-readable codes (api-contract section 5) plus client-side ones. */
export type ApiErrorCode =
  | 'validation_failed'
  | 'unauthorized'
  | 'forbidden'
  | 'not_found'
  | 'turn_in_progress'
  | 'workflow_unavailable'
  | 'payload_too_large'
  | 'unsupported_media_type'
  | 'rate_limited'
  | 'deletion_incomplete'
  | 'guest_expired'
  | 'guest_quota_exceeded'
  | 'guest_access_disabled'
  | 'bot_check_failed'
  | 'internal_error'
  | 'network_error'
  | 'aborted'
  | (string & {});

export class ApiError extends Error {
  readonly status: number;
  readonly code: ApiErrorCode;
  /** Seconds, from the Retry-After header (429, 503 deletion_incomplete). */
  readonly retryAfterSeconds?: number;
  readonly traceId?: string;

  constructor(init: { status: number; code: ApiErrorCode; message?: string; retryAfterSeconds?: number; traceId?: string }) {
    super(init.message ?? init.code);
    this.name = 'ApiError';
    this.status = init.status;
    this.code = init.code;
    if (init.retryAfterSeconds !== undefined) this.retryAfterSeconds = init.retryAfterSeconds;
    if (init.traceId !== undefined) this.traceId = init.traceId;
  }
}

export function isAbortError(e: unknown): boolean {
  return (e instanceof DOMException && e.name === 'AbortError') || (e instanceof ApiError && e.code === 'aborted');
}

export const GUEST_LIMIT_TEXT = 'You have reached the guest limit. Sign in to keep chatting.';

/** User-facing text per code. The server `title`/`detail` are never shown. */
const MESSAGES: Record<string, string> = {
  validation_failed: 'Your request was not accepted. Check the message and try again.',
  unauthorized: 'Your session has expired. Please sign in again.',
  forbidden: 'You do not have access to this application.',
  not_found: 'This conversation no longer exists.',
  turn_in_progress: 'A reply is still being finished, try again.',
  workflow_unavailable: 'This assistant is no longer available.',
  payload_too_large: 'That message is too large to send.',
  unsupported_media_type: 'Something went wrong. Please try again.',
  rate_limited: 'You are sending messages too quickly.',
  deletion_incomplete: 'Deleting your data is taking longer than expected. Please try again in a minute.',
  internal_error: 'Something went wrong. Please try again.',
  guest_expired: 'Your guest session has ended.',
  guest_quota_exceeded: GUEST_LIMIT_TEXT,
  guest_access_disabled: 'Guest access is not available right now.',
  bot_check_failed: 'Guest sign-in could not be verified. Please try again.',
  network_error: 'Could not reach the server. Check your connection and try again.',
};

export function describeApiError(e: ApiError): string {
  if (e.code === 'rate_limited' && e.retryAfterSeconds !== undefined) {
    return `You are sending messages too quickly. Try again in ${e.retryAfterSeconds} seconds.`;
  }
  return MESSAGES[e.code] ?? MESSAGES.internal_error!;
}

/**
 * Maps a failed HTTP response to an ApiError. ProblemDetails (application/problem+json) is switched on `code`;
 * anything else (nginx/ingress HTML) is mapped by status: 413 -> payload_too_large, 5xx -> internal_error.
 */
export async function errorFromResponse(response: Response): Promise<ApiError> {
  const status = response.status;
  const retryHeader = response.headers.get('Retry-After');
  const retryParsed = retryHeader !== null && /^\d+$/.test(retryHeader.trim()) ? Number(retryHeader.trim()) : undefined;
  const contentType = response.headers.get('Content-Type') ?? '';

  let code: ApiErrorCode | undefined;
  let traceId: string | undefined;
  if (contentType.includes('application/problem+json') || contentType.includes('application/json')) {
    try {
      const body: unknown = await response.json();
      if (typeof body === 'object' && body !== null) {
        const b = body as Record<string, unknown>;
        if (typeof b.code === 'string') code = b.code;
        if (typeof b.traceId === 'string') traceId = b.traceId;
      }
    } catch {
      /* fall through to status mapping */
    }
  }

  if (code === undefined) {
    if (status === 401) code = 'unauthorized';
    else if (status === 403) code = 'forbidden';
    else if (status === 404) code = 'not_found';
    else if (status === 413) code = 'payload_too_large';
    else if (status === 429) code = 'rate_limited';
    else if (status === 415) code = 'unsupported_media_type';
    else if (status === 400) code = 'validation_failed';
    else code = 'internal_error';
  }

  const retryAfterSeconds = status === 429 ? (retryParsed ?? 60) : status === 503 && code === 'deletion_incomplete' ? (retryParsed ?? 1) : undefined;
  return new ApiError({ status, code, ...(retryAfterSeconds !== undefined && { retryAfterSeconds }), ...(traceId !== undefined && { traceId }) });
}
