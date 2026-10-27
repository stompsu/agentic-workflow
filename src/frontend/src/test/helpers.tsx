import type { ReactElement } from 'react';
import { render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ApiClient } from '../api/client';
import { ApiContext } from '../api/ApiContext';
import { AuthContext, type AuthContextValue } from '../auth/AuthContext';
import type { Message, Session, SessionDetail, Workflow } from '../api/types';

const encoder = new TextEncoder();

/** A Response whose body emits the given chunks (strings are UTF-8 encoded) one by one. */
export function sseResponse(chunks: (string | Uint8Array)[], init: { status?: number; headers?: Record<string, string> } = {}): Response {
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const c of chunks) controller.enqueue(typeof c === 'string' ? encoder.encode(c) : c);
      controller.close();
    },
  });
  return new Response(stream, {
    status: init.status ?? 200,
    headers: { 'Content-Type': 'text/event-stream; charset=utf-8', ...init.headers },
  });
}

/** A stream that stays open until the request signal aborts (for Stop tests). */
export function openSseResponse(initial: string[], signal: AbortSignal | null | undefined): Response {
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const c of initial) controller.enqueue(encoder.encode(c));
      signal?.addEventListener('abort', () => controller.error(new DOMException('Aborted', 'AbortError')), { once: true });
    },
  });
  return new Response(stream, { status: 200, headers: { 'Content-Type': 'text/event-stream' } });
}

export function frame(id: number, event: string, data: unknown): string {
  return `id: ${id}\nevent: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
}

export function problem(status: number, code: string, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify({ title: 'x', status, code, traceId: 't' }), {
    status,
    headers: { 'Content-Type': 'application/problem+json', ...headers },
  });
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

export const answerStream = (sessionId: string) =>
  [
    frame(1, 'session', { sessionId }),
    frame(2, 'agent', { name: 'Claims specialist' }),
    frame(3, 'token', { text: 'Most claims are settled ' }),
    frame(4, 'token', { text: 'within 10 working days [S1].' }),
    frame(5, 'citation', { ref: 'S1', title: 'Claims handling times', docId: 'claims-handling-0003', url: 'https://kb.example.com/claims/handling' }),
    frame(6, 'done', { messageId: 'msg-assistant-1' }),
  ].join('');

export function makeSession(over: Partial<Session> = {}): Session {
  return {
    id: 's-1',
    workflowId: 'support-ticket',
    title: 'My claim from May',
    createdAt: '2026-10-25T10:15:30.123Z',
    updatedAt: '2026-10-25T10:17:02.001Z',
    ...over,
  };
}

export function makeDetail(over: Partial<SessionDetail> = {}, messages: Message[] = []): SessionDetail {
  return { ...makeSession(), hasMoreMessages: false, messages, ...over };
}

export const WORKFLOWS: Workflow[] = [
  { id: 'support-ticket', displayName: 'Support ticket', description: 'Questions about policies, claims and health assessments.' },
];

export function makeClient(fetchImpl: typeof fetch, over: Partial<ConstructorParameters<typeof ApiClient>[0]> = {}) {
  const getToken = over.getToken ?? (async () => 'test-token');
  return new ApiClient({
    baseUrl: '/api',
    getToken,
    onAuthFailure: () => undefined,
    fetchImpl,
    sleep: async () => undefined,
    ...over,
  });
}

export function fakeAuth(over: Partial<AuthContextValue> = {}): AuthContextValue {
  return {
    status: 'authenticated',
    userName: 'Test User',
    sessionExpired: false,
    login: async () => undefined,
    logout: async () => undefined,
    getToken: async () => 'test-token',
    handleAuthFailure: () => undefined,
    mode: 'social',
    loginAsGuest: async () => undefined,
    endGuest: () => undefined,
    guestNotice: null,
    handleGuestEnded: () => undefined,
    ...over,
  };
}

export function renderWithApi(ui: ReactElement, api: ApiClient, auth: AuthContextValue = fakeAuth()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 0 } } });
  return {
    queryClient,
    ...render(
      <AuthContext.Provider value={auth}>
        <QueryClientProvider client={queryClient}>
          <ApiContext.Provider value={api}>{ui}</ApiContext.Provider>
        </QueryClientProvider>
      </AuthContext.Provider>,
    ),
  };
}

export interface Call {
  method: string;
  url: string;
  headers: Headers;
  body: unknown;
}

/** Route table fake backend: key "METHOD /path" (query string ignored), handler gets the recorded call. */
export function fakeBackend(routes: Record<string, (call: Call) => Response | Promise<Response>>) {
  const calls: Call[] = [];
  const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const method = (init?.method ?? 'GET').toUpperCase();
    const call: Call = {
      method,
      url,
      headers: new Headers(init?.headers),
      body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
    };
    calls.push(call);
    const path = url.split('?')[0]!;
    const handler = routes[`${method} ${path}`];
    if (!handler) return new Response('not found', { status: 404 });
    return handler(call);
  }) as typeof fetch;
  return { fetchImpl, calls };
}
