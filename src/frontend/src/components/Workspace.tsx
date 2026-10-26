import { useRef, useState } from 'react';
import { createPortal, flushSync } from 'react-dom';
import { DEFAULT_BRANDING, type AppInfoConfig } from '../config/config';
import { useAuth } from '../auth/AuthContext';
import { ChatView } from './ChatView';
import { DeleteDataDialog } from './DeleteDataDialog';
import { Sidebar } from './Sidebar';

interface Selection {
  sessionId: string | null;
  /** Changes on explicit navigation (remounts the chat, aborting any running turn). */
  nonce: number;
}

export function Workspace({ title = DEFAULT_BRANDING.title, info = {} }: { title?: string; info?: AppInfoConfig }) {
  const { userName, logout } = useAuth();
  const [selection, setSelection] = useState<Selection>({ sessionId: null, nonce: 0 });
  const [menuOpen, setMenuOpen] = useState(false);
  const [accountOpen, setAccountOpen] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [deletedNotice, setDeletedNotice] = useState(false);
  const accountButton = useRef<HTMLButtonElement>(null);
  const noticeRef = useRef<HTMLDivElement>(null);

  const select = (sessionId: string | null) => {
    setSelection((s) => ({ sessionId, nonce: s.nonce + 1 }));
    setMenuOpen(false);
  };

  function closeDialog() {
    // Commit first: the app root is inert while the dialog is open and inert elements cannot take focus.
    flushSync(() => setConfirmingDelete(false));
    accountButton.current?.focus();
  }

  return (
    <div className="app" inert={confirmingDelete}>
      <a className="skip-link" href="#main">
        Skip to conversation
      </a>
      <header className="topbar">
        <button type="button" className="btn btn-small btn-secondary menu-toggle" aria-expanded={menuOpen} aria-controls="history" onClick={() => setMenuOpen((o) => !o)}>
          History
        </button>
        <h1 className="brand">{title}</h1>
        <span className="spacer" />
        {userName && <span className="user-name">{userName}</span>}
        <div
          className="account"
          onKeyDown={(e) => {
            if (e.key === 'Escape' && accountOpen) {
              setAccountOpen(false);
              accountButton.current?.focus();
            }
          }}
        >
          <button type="button" ref={accountButton} className="btn btn-small btn-secondary" aria-expanded={accountOpen} aria-controls="account-menu" onClick={() => setAccountOpen((o) => !o)}>
            Account
          </button>
          {accountOpen && (
            <div id="account-menu" className="account-menu">
              <button
                type="button"
                className="btn btn-small btn-secondary"
                onClick={() => {
                  setAccountOpen(false);
                  setConfirmingDelete(true);
                }}
              >
                Delete my data
              </button>
            </div>
          )}
        </div>
        <button type="button" className="btn btn-small btn-secondary" onClick={() => void logout()}>
          Sign out
        </button>
      </header>
      <div className="layout">
        <div id="history" className={menuOpen ? 'history history-open' : 'history'}>
          <Sidebar
            activeSessionId={selection.sessionId}
            onSelect={(id) => select(id)}
            onNewChat={() => select(null)}
            {...(info.retentionDays !== undefined && { retentionDays: info.retentionDays })}
            onDeleted={(id) => {
              if (id === selection.sessionId) select(null);
            }}
          />
        </div>
        <main id="main" className="main" tabIndex={-1}>
          {/* The live region is always rendered (empty) so screen readers register it before it is filled. */}
          <div role="status" ref={noticeRef} tabIndex={-1} className="notice-region">
            {deletedNotice && (
              <div className="banner banner-info">
                <span>
                  Your chat history was deleted. Your sign-in account was not deleted.
                  {info.supportContact && <> To have your sign-in account deleted as well, contact {info.supportContact}.</>}
                </span>
                <button type="button" className="btn btn-small btn-secondary" onClick={() => setDeletedNotice(false)}>
                  Dismiss
                </button>
              </div>
            )}
          </div>
          <ChatView
            key={selection.nonce}
            sessionId={selection.sessionId}
            onSessionCreated={(id) => setSelection((s) => ({ ...s, sessionId: id }))}
            onNewChat={() => select(null)}
          />
        </main>
      </div>
      {/* Portal to body: the dialog must sit outside the inert app root. */}
      {confirmingDelete &&
        createPortal(
          <DeleteDataDialog
            // Synchronously leave the current chat: unmounting ChatView aborts its stream (AbortController) before the request.
            onBeforeDelete={() => flushSync(() => setSelection((s) => ({ sessionId: null, nonce: s.nonce + 1 })))}
            onDeleted={() => {
              flushSync(() => {
                setDeletedNotice(true);
                setMenuOpen(false);
                setConfirmingDelete(false);
              });
              // The dialog is gone: move focus to the announced notice.
              noticeRef.current?.focus();
            }}
            onClose={closeDialog}
          />,
          document.body,
        )}
    </div>
  );
}
