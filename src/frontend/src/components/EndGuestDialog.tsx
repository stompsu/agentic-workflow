import { useEffect, useId, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { isAbortError } from '../api/errors';
import { useApi } from '../api/ApiContext';
import type { ProviderConfig } from '../config/config';

export type EndGuestVariant = 'end' | 'signin';

interface Props {
  variant: EndGuestVariant;
  /** Providers offered as confirm actions in the 'signin' variant (empty = a single "Sign in" action). */
  providers: ProviderConfig[];
  /** Called after the confirmation and BEFORE the request: abort any in-flight stream in this tab. */
  onBeforeDelete: () => void;
  /**
   * Called when the guest is done: DELETE /api/me/data returned 204 ('deleted') or failed / gave up ('delete-failed').
   * The guest token is cleared locally in both cases (ADR 0006 section 2). `choice` is the provider picked in 'signin'.
   */
  onFinished: (outcome: 'deleted' | 'delete-failed', choice: { domainHint?: string } | null) => void;
  onClose: () => void;
}

const FOCUSABLE = 'button:not([disabled])';

/** Confirmation + execution of "End guest session" and of "Sign in" from guest mode (ADR 0006 sections 2, 6.5). */
export function EndGuestDialog({ variant, providers, onBeforeDelete, onFinished, onClose }: Props) {
  const api = useApi();
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  const titleId = useId();
  const descId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const controllerRef = useRef<AbortController | null>(null);

  useEffect(() => {
    cancelRef.current?.focus();
    return () => controllerRef.current?.abort();
  }, []);

  useEffect(() => {
    if (busy && dialogRef.current && !dialogRef.current.contains(document.activeElement)) dialogRef.current.focus();
  }, [busy]);

  async function run(choice: { domainHint?: string } | null) {
    if (busy) return;
    setBusy(true);
    onBeforeDelete();
    const controller = new AbortController();
    controllerRef.current = controller;
    const outcome = await api
      .deleteMyDataWithRetry(controller.signal)
      .then((r): 'deleted' | 'delete-failed' => (r === 'deleted' ? 'deleted' : 'delete-failed'))
      // Even if the DELETE failed the guest ends locally; the data expires on its own.
      .catch((e: unknown) => (isAbortError(e) ? null : ('delete-failed' as const)));
    if (outcome === null) return;
    await qc.cancelQueries();
    qc.clear();
    onFinished(outcome, choice);
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'Escape') {
      e.stopPropagation();
      if (!busy) onClose();
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

  const signIn = variant === 'signin';

  return (
    <div className="modal-backdrop">
      <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby={titleId} aria-describedby={descId} className="modal" tabIndex={-1} onKeyDown={onKeyDown}>
        <h2 id={titleId}>{signIn ? 'Sign in?' : 'End guest session?'}</h2>
        <p id={descId}>
          {signIn
            ? 'Signing in starts a separate account. Your guest chats will not be moved to it and will be deleted.'
            : 'This permanently deletes all your guest chats. This cannot be undone.'}
        </p>
        <div role="status" aria-live="polite" className={busy ? 'hint' : 'sr-only'}>
          {busy ? 'Deleting your guest chats...' : ''}
        </div>
        <div className="modal-actions">
          <button type="button" ref={cancelRef} className="btn btn-secondary" aria-disabled={busy} onClick={() => !busy && onClose()}>
            Cancel
          </button>
          {signIn ? (
            providers.length > 0 ? (
              providers.map((p) => (
                <button key={p.id} type="button" className="btn" aria-disabled={busy} onClick={() => void run(p.domainHint ? { domainHint: p.domainHint } : {})}>
                  {p.label}
                </button>
              ))
            ) : (
              <button type="button" className="btn" aria-disabled={busy} onClick={() => void run({})}>
                Sign in
              </button>
            )
          ) : (
            <button type="button" className="btn btn-danger" aria-disabled={busy} onClick={() => void run(null)}>
              End guest session
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
