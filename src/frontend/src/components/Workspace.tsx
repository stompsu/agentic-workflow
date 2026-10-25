import { useState } from 'react';
import { DEFAULT_BRANDING } from '../config/config';
import { useAuth } from '../auth/AuthContext';
import { ChatView } from './ChatView';
import { Sidebar } from './Sidebar';

interface Selection {
  sessionId: string | null;
  /** Changes on explicit navigation (remounts the chat, aborting any running turn). */
  nonce: number;
}

export function Workspace({ title = DEFAULT_BRANDING.title }: { title?: string }) {
  const { userName, logout } = useAuth();
  const [selection, setSelection] = useState<Selection>({ sessionId: null, nonce: 0 });
  const [menuOpen, setMenuOpen] = useState(false);

  const select = (sessionId: string | null) => {
    setSelection((s) => ({ sessionId, nonce: s.nonce + 1 }));
    setMenuOpen(false);
  };

  return (
    <div className="app">
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
            onDeleted={(id) => {
              if (id === selection.sessionId) select(null);
            }}
          />
        </div>
        <main id="main" className="main" tabIndex={-1}>
          <ChatView
            key={selection.nonce}
            sessionId={selection.sessionId}
            onSessionCreated={(id) => setSelection((s) => ({ ...s, sessionId: id }))}
            onNewChat={() => select(null)}
          />
        </main>
      </div>
    </div>
  );
}
