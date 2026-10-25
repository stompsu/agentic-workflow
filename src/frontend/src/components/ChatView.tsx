import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, describeApiError } from '../api/errors';
import { useApi } from '../api/ApiContext';
import { isTurnActive } from '../chat/turnReducer';
import { useChatTurn } from '../chat/useChatTurn';
import { useCountdown } from '../chat/useCountdown';
import { Composer } from './Composer';
import { PersistedMessage, StreamingSegment, UserBubble } from './Messages';
import { WorkflowSelect } from './WorkflowSelect';
import { queryKeys } from '../api/queryKeys';

interface Props {
  /** null = new chat (session is created with the first message). */
  sessionId: string | null;
  onSessionCreated: (id: string) => void;
  onNewChat: () => void;
}

const STOP_FINALIZE_DELAY_MS = 1500;

export function ChatView({ sessionId, onSessionCreated, onNewChat }: Props) {
  const api = useApi();
  const qc = useQueryClient();
  const turn = useChatTurn(api);
  const { state, send, reset, settle } = turn;

  const [chosenWorkflow, setChosenWorkflow] = useState<string | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [localError, setLocalError] = useState<{ message: string; content: string } | null>(null);
  const [draft, setDraft] = useState('');
  const [creating, setCreating] = useState(false);
  const finalizeToken = useRef(0);
  const bottomRef = useRef<HTMLDivElement>(null);

  const workflowsQuery = useQuery({ queryKey: queryKeys.workflows, queryFn: ({ signal }) => api.listWorkflows(signal) });
  const workflows = useMemo(() => workflowsQuery.data ?? [], [workflowsQuery.data]);
  const workflowId = chosenWorkflow ?? workflows[0]?.id ?? '';

  // While a turn is on screen the cached history is frozen so the pending bubbles never duplicate persisted ones.
  const turnOnScreen = state.phase !== 'idle' && !state.settled;
  const sessionQuery = useQuery({
    queryKey: queryKeys.session(sessionId ?? ''),
    queryFn: ({ signal }) => api.getSession(sessionId!, signal),
    enabled: sessionId !== null && !turnOnScreen,
  });
  const session = sessionQuery.data;

  const workflowGone = session !== undefined && workflowsQuery.isSuccess && !workflows.some((w) => w.id === session.workflowId);
  const readOnly = unavailable || workflowGone;

  const blockedSeconds = useCountdown(state.blockedUntil);
  const active = isTurnActive(state);

  useEffect(() => {
    bottomRef.current?.scrollIntoView?.({ block: 'end' });
  }, [state.segments, state.phase, session?.messages.length]);

  const refreshHistory = useCallback(
    async (id: string) => {
      await Promise.allSettled([
        qc.fetchQuery({ queryKey: queryKeys.session(id), queryFn: () => api.getSession(id), staleTime: 0 }),
        qc.invalidateQueries({ queryKey: queryKeys.sessions }),
      ]);
    },
    [api, qc],
  );

  const runTurn = useCallback(
    async (id: string, content: string) => {
      const token = ++finalizeToken.current;
      const result = await send(id, content);
      if (token !== finalizeToken.current) return; // a newer turn took over
      if (result.apiError?.code === 'workflow_unavailable') setUnavailable(true);
      if (!result.accepted) return; // keep the pending bubble + error so the user can retry
      if (result.outcome === 'stopped') {
        // The server persists the partial reply a moment after the abort.
        await new Promise((r) => setTimeout(r, STOP_FINALIZE_DELAY_MS));
        if (token !== finalizeToken.current) return;
      }
      await refreshHistory(id);
      if (token !== finalizeToken.current) return;
      // The history is fresh: drop the live copy. Errors keep their banner, 'stopped' its status text.
      if (result.outcome === 'done') reset();
      else settle();
    },
    [send, reset, settle, refreshHistory],
  );

  async function handleSend(content: string) {
    setLocalError(null);
    let id = sessionId;
    if (id === null) {
      if (!workflowId) return;
      setCreating(true);
      try {
        const created = await api.createSession({ workflowId });
        id = created.id;
        onSessionCreated(id);
        void qc.invalidateQueries({ queryKey: queryKeys.sessions });
      } catch (e) {
        // The draft stays in the composer; Retry re-sends it.
        setLocalError({ message: e instanceof ApiError ? describeApiError(e) : 'Something went wrong. Please try again.', content });
        setCreating(false);
        return;
      }
      setCreating(false);
    }
    // Turn starts now: clear the draft unless the user already typed something else.
    setDraft((d) => (d.trim() === content ? '' : d));
    await runTurn(id, content);
  }

  function handleRetry() {
    if (state.userContent && sessionId) {
      if (state.accepted) void refreshHistory(sessionId); // show the server's view before asking again
      void runTurn(sessionId, state.userContent);
    } else if (state.userContent) void handleSend(state.userContent);
  }

  const error = state.error;
  const streamingNow = active;
  const statusText =
    state.phase === 'connecting'
      ? 'Sending message'
      : state.phase === 'streaming'
        ? `${state.segments.at(-1)?.agentName ?? 'The assistant'} is answering`
        : state.phase === 'done'
          ? 'Answer complete'
          : state.phase === 'stopped'
            ? 'Stopped'
            : '';

  const currentAgent = state.segments.at(-1)?.agentName;
  const workflowName = session ? (workflows.find((w) => w.id === session.workflowId)?.displayName ?? session.workflowId) : null;

  const disabledReason = readOnly
    ? undefined
    : blockedSeconds > 0
      ? `Sending is paused for ${blockedSeconds} s because you are sending too quickly.`
      : undefined;

  return (
    <section className="chat" aria-label="Conversation">
      <header className="chat-header">
        {sessionId === null ? (
          workflowsQuery.isError ? (
            <p role="alert" className="error-text">
              Could not load the assistants.{' '}
              <button type="button" className="link-button" onClick={() => void workflowsQuery.refetch()}>
                Retry
              </button>
            </p>
          ) : workflows.length > 0 ? (
            <WorkflowSelect workflows={workflows} value={workflowId} onChange={setChosenWorkflow} disabled={active} />
          ) : (
            <p className="hint">Loading assistants...</p>
          )
        ) : (
          <h2 className="chat-title">{session?.title ?? 'New chat'}{workflowName ? <span className="chat-workflow"> · {workflowName}</span> : null}</h2>
        )}
      </header>

      <div className="messages" tabIndex={0} aria-label="Messages">
        {sessionId !== null && sessionQuery.isPending && sessionQuery.fetchStatus !== 'idle' && <p className="hint">Loading conversation...</p>}
        {sessionQuery.isError && (
          <p role="alert" className="error-text">
            {sessionQuery.error instanceof ApiError ? describeApiError(sessionQuery.error) : 'Could not load this conversation.'}
          </p>
        )}
        {session?.hasMoreMessages && <p className="hint">Showing the most recent messages.</p>}
        <ol className="message-list">
          {session?.messages.map((m) => <PersistedMessage key={m.id} message={m} />)}
          {turnOnScreen && state.userContent !== null && <UserBubble content={state.userContent} notSent={!state.accepted && state.phase === 'error'} />}
        </ol>
        <div
          className="live"
          aria-live="polite"
          aria-atomic="false"
          aria-busy={streamingNow}
          aria-label={currentAgent ? `Reply from ${currentAgent}` : 'Reply'}
          role="log"
        >
          <ol className="message-list">
            {state.segments.map((seg, i) => (
              <StreamingSegment key={i} segment={seg} index={i} stopped={state.phase === 'stopped' && i === state.segments.length - 1} />
            ))}
          </ol>
        </div>
        {state.phase === 'connecting' && <p className="hint" aria-hidden="true">Waiting for the assistant...</p>}
        {streamingNow && currentAgent && (
          <p className="agent-active" aria-hidden="true">
            <span className="dot" /> {currentAgent} is answering...
          </p>
        )}
        <div ref={bottomRef} />
      </div>

      <div role="status" className="sr-only">
        {statusText}
      </div>

      {error && error.code !== 'workflow_unavailable' && (
        <div role="alert" className="banner banner-error">
          <span>
            {error.message}
            {state.accepted && ' Your earlier message was received and stays in the conversation history; asking again sends it as a new message.'}
          </span>
          {blockedSeconds === 0 && state.userContent && !readOnly && (
            <button type="button" className="btn btn-small" onClick={handleRetry}>
              {state.accepted ? 'Ask again' : 'Retry'}
            </button>
          )}
          <button type="button" className="btn btn-small btn-secondary" onClick={reset}>
            Dismiss
          </button>
        </div>
      )}
      {localError && (
        <div role="alert" className="banner banner-error">
          <span>{localError.message}</span>
          {blockedSeconds === 0 && (
            <button type="button" className="btn btn-small" onClick={() => void handleSend(localError.content)}>
              Retry
            </button>
          )}
        </div>
      )}

      {readOnly ? (
        <div className="banner banner-info" role="status">
          <span>This assistant is no longer available. You can read this conversation, but not continue it.</span>
          <button type="button" className="btn btn-small" onClick={onNewChat}>
            Start a new chat
          </button>
        </div>
      ) : (
        <Composer
          value={draft}
          onChange={setDraft}
          disabled={creating || blockedSeconds > 0 || (sessionId === null && !workflowId)}
          disabledReason={disabledReason}
          busy={active}
          onSend={(c) => void handleSend(c)}
          onStop={turn.stop}
        />
      )}
    </section>
  );
}
