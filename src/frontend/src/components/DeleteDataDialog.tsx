import { useEffect, useId, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, describeApiError, isAbortError } from '../api/errors';
import { useApi } from '../api/ApiContext';
import { queryKeys } from '../api/queryKeys';

type Phase = 'confirm' | 'deleting' | 'incomplete' | 'error';

interface Props {
  /** Called after the confirmation and BEFORE the request: abort any in-flight stream in this tab. */
  onBeforeDelete: () => void;
  /** Called after DELETE /api/me/data returned 204 and the cache was cleared. */
  onDeleted: () => void;
  /** Cancel / close. */
  onClose: () => void;
}

export const INCOMPLETE_TEXT = 'Deleting your data is taking longer than expected. Please try again in a minute.';

// Buttons use aria-disabled (not disabled) while busy so they keep focus and the Tab trap keeps working.
const FOCUSABLE = 'button:not([disabled])';

/** Confirmation + execution of "Delete my data" (api-contract 2.8). Modal, keyboard accessible. */
export function DeleteDataDialog({ onBeforeDelete, onDeleted, onClose }: Props) {
  const api = useApi();
  const qc = useQueryClient();
  const [phase, setPhase] = useState<Phase>('confirm');
  const [message, setMessage] = useState<string | null>(null);
  const titleId = useId();
  const descId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const controllerRef = useRef<AbortController | null>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const retryRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    cancelRef.current?.focus();
    return () => controllerRef.current?.abort();
  }, []);

  // Keep focus inside the modal across phase changes.
  useEffect(() => {
    const dialog = dialogRef.current;
    if (phase === 'incomplete' || phase === 'error') {
      retryRef.current?.focus();
    } else if (phase === 'deleting' && dialog && !dialog.contains(document.activeElement)) {
      dialog.focus();
    }
  }, [phase]);

  async function run() {
    if (phase === 'deleting') return;
    setPhase('deleting');
    setMessage(null);
    onBeforeDelete();
    const controller = new AbortController();
    controllerRef.current = controller;
    try {
      const result = await api.deleteMyDataWithRetry(controller.signal);
      if (result === 'incomplete') {
        setMessage(INCOMPLETE_TEXT);
        setPhase('incomplete');
        return;
      }
    } catch (e) {
      if (isAbortError(e)) return;
      setMessage(e instanceof ApiError ? describeApiError(e) : 'Something went wrong. Please try again.');
      setPhase('error');
      return;
    }
    // Drop every cached session list and message. Mounted observers refetch (empty list).
    await qc.cancelQueries();
    qc.removeQueries({ queryKey: queryKeys.sessionAll });
    await qc.resetQueries({ queryKey: queryKeys.sessions });
    onDeleted();
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'Escape') {
      e.stopPropagation();
      // While deleting, Escape is ignored: a request that is already running must not be abandoned half-way
      // (the user would not know whether their data is gone). Close/Escape work again once it finished or failed.
      if (phase !== 'deleting') onClose();
      return;
    }
    if (e.key !== 'Tab') return;
    const items = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE) ?? []);
    if (items.length === 0) {
      e.preventDefault();
      dialogRef.current?.focus();
      return;
    }
    const first = items[0]!;
    const last = items[items.length - 1]!;
    if (e.shiftKey && (document.activeElement === first || !dialogRef.current?.contains(document.activeElement))) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

  const busy = phase === 'deleting';

  return (
    <div className="modal-backdrop">
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descId}
        className="modal"
        tabIndex={-1}
        onKeyDown={onKeyDown}
      >
        <h2 id={titleId}>Delete my data?</h2>
        <div id={descId}>
          <p>All your chat history in this app (every conversation and message) will be permanently deleted. This cannot be undone.</p>
          <p>Your sign-in account is not deleted, and you stay signed in.</p>
        </div>
        <div role="status" aria-live="polite" className={busy ? 'hint' : 'sr-only'}>
          {busy ? 'Deleting your data...' : ''}
        </div>
        {message && (
          <p role="alert" className="error-text">
            {message}
          </p>
        )}
        <div className="modal-actions">
          <button type="button" ref={cancelRef} className="btn btn-secondary" aria-disabled={busy} onClick={() => !busy && onClose()}>
            {phase === 'confirm' ? 'Cancel' : 'Close'}
          </button>
          <button type="button" ref={retryRef} className="btn btn-danger" aria-disabled={busy} onClick={() => void run()}>
            {phase === 'incomplete' || phase === 'error' ? 'Try again' : 'Delete my data'}
          </button>
        </div>
      </div>
    </div>
  );
}
