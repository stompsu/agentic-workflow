import { useState } from 'react';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, describeApiError } from '../api/errors';
import { useApi } from '../api/ApiContext';
import { queryKeys } from '../api/queryKeys';
import type { Session } from '../api/types';

interface Props {
  activeSessionId: string | null;
  onSelect: (id: string) => void;
  onNewChat: () => void;
  onDeleted: (id: string) => void;
}

const PAGE_SIZE = 20;

function formatDate(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

export function Sidebar({ activeSessionId, onSelect, onNewChat, onDeleted }: Props) {
  const api = useApi();
  const qc = useQueryClient();
  const [confirmId, setConfirmId] = useState<string | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const query = useInfiniteQuery({
    queryKey: queryKeys.sessions,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) => api.listSessions({ pageSize: PAGE_SIZE, continuationToken: pageParam }, signal),
    getNextPageParam: (last) => last.continuationToken ?? undefined,
  });

  const sessions: Session[] = query.data?.pages.flatMap((p) => p.items) ?? [];

  async function confirmDelete(id: string) {
    setDeleteError(null);
    try {
      await api.deleteSession(id);
    } catch (e) {
      // 404 means it is already gone: treat as deleted.
      if (!(e instanceof ApiError && e.code === 'not_found')) {
        setDeleteError(e instanceof ApiError ? describeApiError(e) : 'Could not delete the conversation.');
        setConfirmId(null);
        return;
      }
    }
    setConfirmId(null);
    qc.removeQueries({ queryKey: queryKeys.session(id) });
    await qc.invalidateQueries({ queryKey: queryKeys.sessions });
    onDeleted(id);
  }

  return (
    <nav className="sidebar" aria-label="Conversation history">
      <button type="button" className="btn btn-block" onClick={onNewChat}>
        New chat
      </button>
      {deleteError && (
        <p role="alert" className="error-text">
          {deleteError}
        </p>
      )}
      {query.isPending && <p className="hint">Loading history...</p>}
      {query.isError && (
        <p role="alert" className="error-text">
          Could not load history.{' '}
          <button type="button" className="link-button" onClick={() => void query.refetch()}>
            Retry
          </button>
        </p>
      )}
      {query.isSuccess && sessions.length === 0 && <p className="hint">No conversations yet.</p>}
      <ul className="session-list">
        {sessions.map((s) => {
          const title = s.title ?? 'New chat';
          const active = s.id === activeSessionId;
          return (
            <li key={s.id} className={active ? 'session session-active' : 'session'}>
              <button type="button" className="session-open" aria-current={active ? 'true' : undefined} onClick={() => onSelect(s.id)}>
                <span className="session-title">{title}</span>
                <span className="session-date">{formatDate(s.updatedAt)}</span>
              </button>
              {confirmId === s.id ? (
                <span className="session-confirm">
                  <button type="button" className="btn btn-small btn-danger" onClick={() => void confirmDelete(s.id)}>
                    Confirm delete
                  </button>
                  <button type="button" className="btn btn-small btn-secondary" onClick={() => setConfirmId(null)}>
                    Cancel
                  </button>
                </span>
              ) : (
                <button type="button" className="icon-button" aria-label={`Delete conversation: ${title}`} onClick={() => setConfirmId(s.id)}>
                  &times;
                </button>
              )}
            </li>
          );
        })}
      </ul>
      {query.hasNextPage && (
        <button type="button" className="btn btn-secondary btn-block" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
          {query.isFetchingNextPage ? 'Loading...' : 'Load more'}
        </button>
      )}
    </nav>
  );
}
