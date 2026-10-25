import { useCallback, useEffect, useReducer, useRef } from 'react';
import { ApiError, describeApiError, isAbortError } from '../api/errors';
import type { ApiClient } from '../api/client';
import { StreamProtocolError } from '../api/sse';
import { initialTurnState, turnReducer, type TurnState } from './turnReducer';

export type TurnOutcome = 'done' | 'error' | 'stopped';
export interface TurnResult {
  outcome: TurnOutcome;
  /** The server accepted the turn (user message persisted) even if the turn later failed or was stopped. */
  accepted: boolean;
  /** Set when the turn failed with an ApiError before the stream started. */
  apiError?: ApiError;
}

export interface UseChatTurn {
  state: TurnState;
  send: (sessionId: string, content: string) => Promise<TurnResult>;
  stop: () => void;
  reset: () => void;
  /** Hide the live bubbles after the history was refetched, keeping the error/status. */
  settle: () => void;
}

const CONNECTION_LOST = 'The connection was interrupted. Please try again.';
const INTERNAL = 'Something went wrong. Please try again.';

/** Runs one chat turn: POST message, consume SSE events, support stop (AbortController). */
export function useChatTurn(api: ApiClient): UseChatTurn {
  const [state, dispatch] = useReducer(turnReducer, initialTurnState);
  const controllerRef = useRef<AbortController | null>(null);

  useEffect(
    () => () => {
      controllerRef.current?.abort();
    },
    [],
  );

  const send = useCallback(
    async (sessionId: string, content: string): Promise<TurnResult> => {
      controllerRef.current?.abort();
      const controller = new AbortController();
      controllerRef.current = controller;
      dispatch({ type: 'start', content });

      let accepted = false;
      let terminal: 'done' | 'error' | null = null;
      try {
        for await (const event of api.sendMessage(sessionId, content, controller.signal)) {
          if (event.type === 'session') accepted = true;
          if (event.type === 'done' || event.type === 'error') terminal = event.type;
          dispatch({ type: 'event', event });
        }
      } catch (e) {
        if (isAbortError(e) || controller.signal.aborted) {
          dispatch({ type: 'stopped' });
          return { outcome: 'stopped', accepted };
        }
        if (e instanceof ApiError) {
          const blockedUntil = e.code === 'rate_limited' && e.retryAfterSeconds !== undefined ? Date.now() + e.retryAfterSeconds * 1000 : undefined;
          dispatch({
            type: 'fail',
            error: { code: e.code, message: describeApiError(e), status: e.status },
            ...(blockedUntil !== undefined && { blockedUntil }),
          });
          return { outcome: 'error', accepted, apiError: e };
        }
        const code = e instanceof StreamProtocolError ? 'internal_error' : 'stream_interrupted';
        dispatch({ type: 'fail', error: { code, message: code === 'internal_error' ? INTERNAL : CONNECTION_LOST } });
        return { outcome: 'error', accepted };
      } finally {
        if (controllerRef.current === controller) controllerRef.current = null;
      }

      if (!terminal) {
        // Stream closed without done/error (network drop, proxy timeout).
        dispatch({ type: 'fail', error: { code: 'stream_interrupted', message: CONNECTION_LOST } });
        return { outcome: 'error', accepted };
      }
      return { outcome: terminal, accepted };
    },
    [api],
  );

  const stop = useCallback(() => controllerRef.current?.abort(), []);
  const reset = useCallback(() => dispatch({ type: 'reset' }), []);

  const settle = useCallback(() => dispatch({ type: 'settle' }), []);

  return { state, send, stop, reset, settle };
}
