import { describe, expect, it, vi } from 'vitest';
import { ApiError, describeApiError } from './errors';
import type { StreamEvent } from './types';
import { answerStream, fakeBackend, frame, json, makeClient, makeSession, problem, sseResponse } from '../test/helpers';

async function drain(gen: AsyncGenerator<StreamEvent>): Promise<StreamEvent[]> {
  const out: StreamEvent[] = [];
  for await (const e of gen) out.push(e);
  return out;
}

describe('ApiClient', () => {
  it('sends Authorization: Bearer on every call', async () => {
    const { fetchImpl, calls } = fakeBackend({
      'GET /api/workflows': () => json({ items: [] }),
      'GET /api/sessions': () => json({ items: [], continuationToken: null }),
      'GET /api/sessions/s-1': () => json({ ...makeSession(), hasMoreMessages: false, messages: [] }),
      'POST /api/sessions': () => json(makeSession(), 201),
      'DELETE /api/sessions/s-1': () => new Response(null, { status: 204 }),
      'POST /api/sessions/s-1/messages': () => sseResponse([answerStream('s-1')]),
    });
    const api = makeClient(fetchImpl);
    await api.listWorkflows();
    await api.listSessions();
    await api.getSession('s-1');
    await api.createSession({ workflowId: 'support-ticket' });
    await api.deleteSession('s-1');
    await drain(api.sendMessage('s-1', 'hi'));
    expect(calls).toHaveLength(6);
    for (const c of calls) expect(c.headers.get('Authorization')).toBe('Bearer test-token');
  });

  it('pages sessions with the opaque continuation token', async () => {
    const { fetchImpl, calls } = fakeBackend({ 'GET /api/sessions': () => json({ items: [], continuationToken: null }) });
    await makeClient(fetchImpl).listSessions({ pageSize: 20, continuationToken: 'abc_-' });
    expect(calls[0]?.url).toBe('/api/sessions?pageSize=20&continuationToken=abc_-');
  });

  it('sends the message body and SSE accept header', async () => {
    const { fetchImpl, calls } = fakeBackend({ 'POST /api/sessions/s-1/messages': () => sseResponse([answerStream('s-1')]) });
    await drain(makeClient(fetchImpl).sendMessage('s-1', 'How long?'));
    expect(calls[0]?.body).toEqual({ content: 'How long?' });
    expect(calls[0]?.headers.get('Accept')).toBe('text/event-stream');
    expect(calls[0]?.headers.get('Content-Type')).toBe('application/json');
  });

  it('on 401 refreshes the token once and retries with the new token', async () => {
    const getToken = vi.fn(async (force?: boolean) => (force ? 'fresh' : 'stale'));
    let n = 0;
    const { fetchImpl, calls } = fakeBackend({
      'GET /api/workflows': () => (n++ === 0 ? problem(401, 'unauthorized') : json({ items: [{ id: 'a', displayName: 'A', description: '' }] })),
    });
    const onAuthFailure = vi.fn();
    const api = makeClient(fetchImpl, { getToken, onAuthFailure });
    expect(await api.listWorkflows()).toHaveLength(1);
    expect(getToken).toHaveBeenNthCalledWith(1, false);
    expect(getToken).toHaveBeenNthCalledWith(2, true);
    expect(calls[1]?.headers.get('Authorization')).toBe('Bearer fresh');
    expect(onAuthFailure).not.toHaveBeenCalled();
  });

  it('redirects to login (onAuthFailure) when 401 persists after the refresh', async () => {
    const { fetchImpl, calls } = fakeBackend({ 'GET /api/workflows': () => problem(401, 'unauthorized') });
    const onAuthFailure = vi.fn();
    const api = makeClient(fetchImpl, { onAuthFailure });
    await expect(api.listWorkflows()).rejects.toMatchObject({ status: 401, code: 'unauthorized' });
    expect(calls).toHaveLength(2);
    expect(onAuthFailure).toHaveBeenCalledOnce();
  });

  it('maps ProblemDetails by code', async () => {
    const { fetchImpl } = fakeBackend({
      'GET /api/sessions/gone': () => problem(404, 'not_found'),
      'POST /api/sessions': () => problem(400, 'validation_failed'),
    });
    const api = makeClient(fetchImpl);
    await expect(api.getSession('gone')).rejects.toMatchObject({ status: 404, code: 'not_found', traceId: 't' });
    await expect(api.createSession({ workflowId: 'nope' })).rejects.toMatchObject({ code: 'validation_failed' });
  });

  it('maps 429 with Retry-After (seconds)', async () => {
    const { fetchImpl } = fakeBackend({ 'POST /api/sessions/s-1/messages': () => problem(429, 'rate_limited', { 'Retry-After': '17' }) });
    const err = await drain(makeClient(fetchImpl).sendMessage('s-1', 'x')).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 429, code: 'rate_limited', retryAfterSeconds: 17 });
    expect(describeApiError(err as ApiError)).toContain('17 seconds');
  });

  it('maps non-ProblemDetails bodies by status (nginx 413, 502/503/504)', async () => {
    const html = (status: number) => new Response('<html>nginx</html>', { status, headers: { 'Content-Type': 'text/html' } });
    const { fetchImpl } = fakeBackend({
      'POST /api/sessions': () => html(413),
      'GET /api/workflows': () => html(502),
      'GET /api/sessions': () => html(503),
    });
    const api = makeClient(fetchImpl);
    await expect(api.createSession({ workflowId: 'x' })).rejects.toMatchObject({ status: 413, code: 'payload_too_large' });
    await expect(api.listWorkflows()).rejects.toMatchObject({ status: 502, code: 'internal_error' });
    await expect(api.listSessions()).rejects.toMatchObject({ status: 503, code: 'internal_error' });
  });

  it('maps a network failure to network_error', async () => {
    const api = makeClient((async () => {
      throw new TypeError('Failed to fetch');
    }) as typeof fetch);
    await expect(api.listWorkflows()).rejects.toMatchObject({ code: 'network_error' });
  });

  describe('sendMessage 409 turn_in_progress', () => {
    it('retries ONCE after 1 s and then streams', async () => {
      let n = 0;
      const sleep = vi.fn(async () => undefined);
      const { fetchImpl, calls } = fakeBackend({
        'POST /api/sessions/s-1/messages': () => (n++ === 0 ? problem(409, 'turn_in_progress') : sseResponse([answerStream('s-1')])),
      });
      const events = await drain(makeClient(fetchImpl, { sleep }).sendMessage('s-1', 'x'));
      expect(sleep).toHaveBeenCalledExactlyOnceWith(1000, undefined);
      expect(calls).toHaveLength(2);
      expect(events.at(-1)?.type).toBe('done');
    });

    it('gives up after the single retry', async () => {
      const { fetchImpl, calls } = fakeBackend({ 'POST /api/sessions/s-1/messages': () => problem(409, 'turn_in_progress') });
      const err = await drain(makeClient(fetchImpl).sendMessage('s-1', 'x')).catch((e: unknown) => e);
      expect(err).toMatchObject({ status: 409, code: 'turn_in_progress' });
      expect(calls).toHaveLength(2);
      expect(describeApiError(err as ApiError)).toBe('A reply is still being finished, try again.');
    });

    it('does not retry 409 workflow_unavailable', async () => {
      const { fetchImpl, calls } = fakeBackend({ 'POST /api/sessions/s-1/messages': () => problem(409, 'workflow_unavailable') });
      await expect(drain(makeClient(fetchImpl).sendMessage('s-1', 'x'))).rejects.toMatchObject({ code: 'workflow_unavailable' });
      expect(calls).toHaveLength(1);
    });
  });

  it('stops reading after the terminal event', async () => {
    const { fetchImpl } = fakeBackend({
      'POST /api/sessions/s-1/messages': () => sseResponse([frame(1, 'session', { sessionId: 's-1' }), frame(2, 'error', { code: 'timeout', message: 'm' }), frame(3, 'token', { text: 'late' })]),
    });
    const events = await drain(makeClient(fetchImpl).sendMessage('s-1', 'x'));
    expect(events.map((e) => e.type)).toEqual(['session', 'error']);
  });
});
