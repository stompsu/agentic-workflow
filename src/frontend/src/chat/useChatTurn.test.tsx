import { describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { useChatTurn } from './useChatTurn';
import { answerStream, fakeBackend, frame, makeClient, openSseResponse, problem, sseResponse } from '../test/helpers';

describe('useChatTurn', () => {
  it('accumulates agent, tokens and citations and finishes with done', async () => {
    const { fetchImpl } = fakeBackend({ 'POST /api/sessions/s-1/messages': () => sseResponse([answerStream('s-1')]) });
    const { result } = renderHook(() => useChatTurn(makeClient(fetchImpl)));
    let outcome;
    await act(async () => {
      outcome = await result.current.send('s-1', 'How long?');
    });
    expect(outcome).toEqual({ outcome: 'done', accepted: true });
    const s = result.current.state;
    expect(s.phase).toBe('done');
    expect(s.userContent).toBe('How long?');
    expect(s.segments).toHaveLength(1);
    expect(s.segments[0]?.agentName).toBe('Claims specialist');
    expect(s.segments[0]?.text).toBe('Most claims are settled within 10 working days [S1].');
    expect(s.segments[0]?.citations[0]?.ref).toBe('S1');
    expect(s.messageId).toBe('msg-assistant-1');
  });

  it('starts a new visual segment when a second agent event arrives', async () => {
    const body = [
      frame(1, 'session', { sessionId: 's-1' }),
      frame(2, 'agent', { name: 'A' }),
      frame(3, 'token', { text: 'one' }),
      frame(4, 'agent', { name: 'B' }),
      frame(5, 'token', { text: 'two' }),
      frame(6, 'done', { messageId: 'm' }),
    ].join('');
    const { fetchImpl } = fakeBackend({ 'POST /api/sessions/s-1/messages': () => sseResponse([body]) });
    const { result } = renderHook(() => useChatTurn(makeClient(fetchImpl)));
    await act(async () => void (await result.current.send('s-1', 'x')));
    expect(result.current.state.segments.map((s) => [s.agentName, s.text])).toEqual([
      ['A', 'one'],
      ['B', 'two'],
    ]);
  });

  it('surfaces the stream error event (user-safe server message) as accepted failure; retry succeeds', async () => {
    let n = 0;
    const { fetchImpl } = fakeBackend({
      'POST /api/sessions/s-1/messages': () =>
        n++ === 0
          ? sseResponse([
              frame(1, 'session', { sessionId: 's-1' }),
              frame(2, 'agent', { name: 'Policy specialist' }),
              frame(3, 'token', { text: 'Your policy covers ' }),
              frame(4, 'error', { code: 'upstream_throttled', message: 'The service is busy. Please try again in a moment.' }),
            ])
          : sseResponse([answerStream('s-1')]),
    });
    const { result } = renderHook(() => useChatTurn(makeClient(fetchImpl)));
    await act(async () => void (await result.current.send('s-1', 'q')));
    expect(result.current.state.phase).toBe('error');
    expect(result.current.state.accepted).toBe(true);
    expect(result.current.state.error).toEqual({ code: 'upstream_throttled', message: 'The service is busy. Please try again in a moment.' });
    expect(result.current.state.segments[0]?.text).toBe('Your policy covers ');

    await act(async () => void (await result.current.send('s-1', 'q')));
    expect(result.current.state.phase).toBe('done');
    expect(result.current.state.error).toBeNull();
  });

  it('stop aborts the request and keeps the partial text', async () => {
    let signal: AbortSignal | null | undefined;
    const wrapped = (async (_input: RequestInfo | URL, init?: RequestInit) => {
      signal = init?.signal;
      return openSseResponse([frame(1, 'session', { sessionId: 's-1' }), frame(2, 'agent', { name: 'A' }), frame(3, 'token', { text: 'partial' })], init?.signal);
    }) as typeof fetch;
    const { result } = renderHook(() => useChatTurn(makeClient(wrapped)));
    let promise!: Promise<unknown>;
    act(() => {
      promise = result.current.send('s-1', 'x');
    });
    await waitFor(() => expect(result.current.state.segments[0]?.text).toBe('partial'));
    expect(result.current.state.phase).toBe('streaming');
    act(() => result.current.stop());
    await act(async () => void (await promise));
    expect(signal?.aborted).toBe(true);
    expect(result.current.state.phase).toBe('stopped');
    expect(result.current.state.segments[0]?.text).toBe('partial');
    expect(result.current.state.error).toBeNull();
  });

  it('reports an interrupted connection when the stream ends without a terminal event', async () => {
    const { fetchImpl } = fakeBackend({
      'POST /api/sessions/s-1/messages': () => sseResponse([frame(1, 'session', { sessionId: 's-1' }), frame(2, 'agent', { name: 'A' }), frame(3, 'token', { text: 'x' })]),
    });
    const { result } = renderHook(() => useChatTurn(makeClient(fetchImpl)));
    await act(async () => void (await result.current.send('s-1', 'x')));
    expect(result.current.state.phase).toBe('error');
    expect(result.current.state.error?.code).toBe('stream_interrupted');
    expect(result.current.state.accepted).toBe(true);
  });

  it('409 turn_in_progress: retries once after 1 s then succeeds', async () => {
    let n = 0;
    const sleep = vi.fn(async () => undefined);
    const { fetchImpl } = fakeBackend({
      'POST /api/sessions/s-1/messages': () => (n++ === 0 ? problem(409, 'turn_in_progress') : sseResponse([answerStream('s-1')])),
    });
    const { result } = renderHook(() => useChatTurn(makeClient(fetchImpl, { sleep })));
    await act(async () => void (await result.current.send('s-1', 'x')));
    expect(sleep).toHaveBeenCalledWith(1000, expect.anything());
    expect(result.current.state.phase).toBe('done');
  });

  it('409 twice shows the "still being finished" message and keeps the turn not accepted', async () => {
    const { fetchImpl } = fakeBackend({ 'POST /api/sessions/s-1/messages': () => problem(409, 'turn_in_progress') });
    const { result } = renderHook(() => useChatTurn(makeClient(fetchImpl)));
    await act(async () => void (await result.current.send('s-1', 'x')));
    expect(result.current.state.error?.message).toBe('A reply is still being finished, try again.');
    expect(result.current.state.accepted).toBe(false);
  });

  it('429 sets blockedUntil from Retry-After and keeps it after reset', async () => {
    const now = Date.now();
    const { fetchImpl } = fakeBackend({ 'POST /api/sessions/s-1/messages': () => problem(429, 'rate_limited', { 'Retry-After': '30' }) });
    const { result } = renderHook(() => useChatTurn(makeClient(fetchImpl)));
    await act(async () => void (await result.current.send('s-1', 'x')));
    const until = result.current.state.blockedUntil;
    expect(until).not.toBeNull();
    expect(until! - now).toBeGreaterThanOrEqual(29_000);
    expect(until! - now).toBeLessThan(32_000);
    expect(result.current.state.error?.message).toContain('30 seconds');
    act(() => result.current.reset());
    expect(result.current.state.blockedUntil).toBe(until);
    expect(result.current.state.phase).toBe('idle');
  });
});
