import { ApiError, errorFromResponse } from './errors';
import { decodeEvent, readSseFrames } from './sse';
import type { Session, SessionDetail, SessionPage, StreamEvent, Workflow, WorkflowList } from './types';

export interface ApiClientOptions {
  /** Base path, e.g. "/api". */
  baseUrl: string;
  /** Returns an access token for the API scope; `forceRefresh` bypasses the cache. */
  getToken: (forceRefresh?: boolean) => Promise<string>;
  /** Called when a request is still 401 after one forced token refresh: the app goes back to the login page. */
  onAuthFailure: () => void;
  fetchImpl?: typeof fetch;
  /** Abortable delay; injectable for tests. */
  sleep?: (ms: number, signal?: AbortSignal) => Promise<void>;
}

export const TURN_IN_PROGRESS_RETRY_MS = 1000;

export function defaultSleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('Aborted', 'AbortError'));
      return;
    }
    const t = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(t);
      reject(new DOMException('Aborted', 'AbortError'));
    };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

export class ApiClient {
  private readonly fetchImpl: typeof fetch;
  private readonly sleep: (ms: number, signal?: AbortSignal) => Promise<void>;

  constructor(private readonly options: ApiClientOptions) {
    this.fetchImpl = options.fetchImpl ?? ((...args) => fetch(...args));
    this.sleep = options.sleep ?? defaultSleep;
  }

  /**
   * fetch with `Authorization: Bearer` on every call. 401: refresh the token once (forceRefresh) and retry;
   * still 401 -> onAuthFailure (redirect to login) and ApiError(unauthorized).
   */
  private async authedFetch(path: string, init: RequestInit = {}): Promise<Response> {
    const send = async (forceRefresh: boolean): Promise<Response> => {
      const token = await this.options.getToken(forceRefresh);
      const headers = new Headers(init.headers);
      headers.set('Authorization', `Bearer ${token}`);
      try {
        return await this.fetchImpl(`${this.options.baseUrl}${path}`, { ...init, headers, cache: 'no-store' });
      } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') throw e;
        throw new ApiError({ status: 0, code: 'network_error' });
      }
    };

    let response = await send(false);
    if (response.status === 401) {
      await response.body?.cancel().catch(() => undefined);
      try {
        response = await send(true);
      } catch (e) {
        if (e instanceof ApiError) throw e;
        this.options.onAuthFailure();
        throw new ApiError({ status: 401, code: 'unauthorized' });
      }
      if (response.status === 401) {
        await response.body?.cancel().catch(() => undefined);
        this.options.onAuthFailure();
        throw new ApiError({ status: 401, code: 'unauthorized' });
      }
    }
    return response;
  }

  private async json<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await this.authedFetch(path, init);
    if (!response.ok) throw await errorFromResponse(response);
    return (await response.json()) as T;
  }

  async listWorkflows(signal?: AbortSignal): Promise<Workflow[]> {
    const body = await this.json<WorkflowList>('/workflows', { headers: { Accept: 'application/json' }, ...(signal && { signal }) });
    return body.items;
  }

  listSessions(params: { pageSize?: number; continuationToken?: string | null } = {}, signal?: AbortSignal): Promise<SessionPage> {
    const q = new URLSearchParams();
    if (params.pageSize !== undefined) q.set('pageSize', String(params.pageSize));
    if (params.continuationToken) q.set('continuationToken', params.continuationToken);
    const qs = q.size > 0 ? `?${q.toString()}` : '';
    return this.json<SessionPage>(`/sessions${qs}`, { headers: { Accept: 'application/json' }, ...(signal && { signal }) });
  }

  getSession(sessionId: string, signal?: AbortSignal): Promise<SessionDetail> {
    return this.json<SessionDetail>(`/sessions/${encodeURIComponent(sessionId)}`, {
      headers: { Accept: 'application/json' },
      ...(signal && { signal }),
    });
  }

  createSession(input: { workflowId: string; title?: string }, signal?: AbortSignal): Promise<Session> {
    return this.json<Session>('/sessions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(input),
      ...(signal && { signal }),
    });
  }

  async deleteSession(sessionId: string): Promise<void> {
    const response = await this.authedFetch(`/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
    if (!response.ok) throw await errorFromResponse(response);
  }

  private async openMessageStream(sessionId: string, content: string, signal?: AbortSignal): Promise<Response> {
    const response = await this.authedFetch(`/sessions/${encodeURIComponent(sessionId)}/messages`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
      body: JSON.stringify({ content }),
      ...(signal && { signal }),
    });
    if (!response.ok) throw await errorFromResponse(response);
    return response;
  }

  /**
   * Sends a user message and yields the typed SSE events of the assistant turn (stops after the terminal event).
   * HTTP failures before the stream starts throw ApiError. 409 turn_in_progress is retried ONCE after 1 s
   * (stop-then-send, api-contract 2.6). Abort (signal) ends the generator by throwing an AbortError.
   */
  async *sendMessage(sessionId: string, content: string, signal?: AbortSignal): AsyncGenerator<StreamEvent> {
    let response: Response;
    try {
      response = await this.openMessageStream(sessionId, content, signal);
    } catch (e) {
      if (!(e instanceof ApiError && e.code === 'turn_in_progress')) throw e;
      await this.sleep(TURN_IN_PROGRESS_RETRY_MS, signal);
      response = await this.openMessageStream(sessionId, content, signal);
    }
    if (!response.body) throw new ApiError({ status: response.status, code: 'internal_error' });

    for await (const frame of readSseFrames(response.body, signal)) {
      const event = decodeEvent(frame);
      if (!event) continue;
      yield event;
      if (event.type === 'done' || event.type === 'error') return;
    }
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
  }
}
