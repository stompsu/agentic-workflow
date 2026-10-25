import { useState } from 'react';
import type { ProviderConfig } from '../config/config';
import { DEFAULT_BRANDING, type BrandingConfig } from '../config/config';
import { useAuth } from '../auth/AuthContext';

export function LoginPage({ providers, branding = DEFAULT_BRANDING }: { providers: ProviderConfig[]; branding?: BrandingConfig }) {
  const { login, sessionExpired } = useAuth();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function start(domainHint?: string) {
    setError(null);
    setPending(true);
    try {
      await login(domainHint);
    } catch {
      setError('Sign-in could not be started. Please try again.');
      setPending(false);
    }
  }

  return (
    <main className="login">
      <h1>{branding.title}</h1>
      <p>{branding.tagline}</p>
      {sessionExpired && (
        <p role="alert" className="banner banner-info">
          Your session has expired. Please sign in again.
        </p>
      )}
      {error && (
        <p role="alert" className="banner banner-error">
          {error}
        </p>
      )}
      <div className="provider-buttons">
        {providers.length > 0 ? (
          providers.map((p) => (
            <button key={p.id} type="button" className="btn btn-provider" disabled={pending} onClick={() => void start(p.domainHint)}>
              {p.label}
            </button>
          ))
        ) : (
          <button type="button" className="btn btn-provider" disabled={pending} onClick={() => void start()}>
            Sign in
          </button>
        )}
      </div>
    </main>
  );
}
