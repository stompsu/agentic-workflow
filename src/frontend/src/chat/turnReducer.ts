import type { Citation, StreamEvent } from '../api/types';

export interface Segment {
  agentName: string | null;
  text: string;
  citations: Citation[];
}

export interface TurnError {
  code: string;
  message: string;
  status?: number;
}

export type TurnPhase = 'idle' | 'connecting' | 'streaming' | 'done' | 'stopped' | 'error';

export interface TurnState {
  phase: TurnPhase;
  /** The user message of this turn (shown immediately, before the server persisted it). */
  userContent: string | null;
  /** True once the `session` event arrived: the server persisted the user message. */
  accepted: boolean;
  segments: Segment[];
  error: TurnError | null;
  /** Epoch ms until which sending is disabled (HTTP 429 Retry-After). Survives reset. */
  blockedUntil: number | null;
  messageId: string | null;
  /** The turn is persisted and the history refetched: live bubbles are hidden, the error/status stays. */
  settled: boolean;
}

export const initialTurnState: TurnState = {
  phase: 'idle',
  userContent: null,
  accepted: false,
  segments: [],
  error: null,
  blockedUntil: null,
  messageId: null,
  settled: false,
};

export type TurnAction =
  | { type: 'start'; content: string }
  | { type: 'event'; event: StreamEvent }
  | { type: 'fail'; error: TurnError; blockedUntil?: number }
  | { type: 'stopped' }
  | { type: 'settle' }
  | { type: 'reset' };

export function turnReducer(state: TurnState, action: TurnAction): TurnState {
  switch (action.type) {
    case 'start':
      return { ...initialTurnState, phase: 'connecting', userContent: action.content, blockedUntil: null };
    case 'reset':
      return { ...initialTurnState, blockedUntil: state.blockedUntil };
    case 'stopped':
      return { ...state, phase: 'stopped' };
    case 'settle':
      return { ...state, settled: true, segments: [] };
    case 'fail':
      return {
        ...state,
        phase: 'error',
        error: action.error,
        blockedUntil: action.blockedUntil ?? state.blockedUntil,
      };
    case 'event':
      return applyEvent(state, action.event);
  }
}

function applyEvent(state: TurnState, event: StreamEvent): TurnState {
  switch (event.type) {
    case 'session':
      return { ...state, accepted: true, phase: 'streaming' };
    case 'agent':
      // v1: one segment per turn; a second `agent` starts a new visual segment (api-contract 4.3).
      return { ...state, phase: 'streaming', segments: [...state.segments, { agentName: event.name, text: '', citations: [] }] };
    case 'token': {
      const segments = state.segments.length > 0 ? [...state.segments] : [{ agentName: null, text: '', citations: [] }];
      const last = segments[segments.length - 1]!;
      segments[segments.length - 1] = { ...last, text: last.text + event.text };
      return { ...state, phase: 'streaming', segments };
    }
    case 'citation': {
      const segments = state.segments.length > 0 ? [...state.segments] : [{ agentName: null, text: '', citations: [] }];
      const last = segments[segments.length - 1]!;
      if (last.citations.some((c) => c.ref === event.citation.ref)) return state;
      segments[segments.length - 1] = { ...last, citations: [...last.citations, event.citation] };
      return { ...state, segments };
    }
    case 'done':
      return { ...state, phase: 'done', messageId: event.messageId };
    case 'error':
      return { ...state, phase: 'error', error: { code: event.code, message: event.message } };
  }
}

export function isTurnActive(state: TurnState): boolean {
  return state.phase === 'connecting' || state.phase === 'streaming';
}
