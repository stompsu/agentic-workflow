import { useEffect, useMemo, useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ApiClient } from './api/client';
import { ApiContext } from './api/ApiContext';
import { ApiError } from './api/errors';
import { AuthProvider } from './auth/AuthProvider';
import { useAuth } from './auth/AuthContext';
import { loadConfig, type AppConfig, type AppInfoConfig, type ProviderConfig } from './config/config';
import { LoginPage } from './components/LoginPage';
import { Workspace } from './components/Workspace';

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        refetchOnWindowFocus: false,
        // 4xx will not get better by retrying; transient failures retry twice.
        retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
      },
    },
  });
}

/** Signed-in tree: a fresh QueryClient and ApiClient per sign-in so no data survives a sign-out. */
export function Authenticated({
  apiBaseUrl,
  title,
  info,
  providers,
}: {
  apiBaseUrl: string;
  title?: string;
  info?: AppInfoConfig;
  providers?: ProviderConfig[];
}) {
  const auth = useAuth();
  const { getToken, handleAuthFailure, handleGuestEnded, mode } = auth;
  const isGuest = mode === 'guest';
  const [queryClient] = useState(createQueryClient);
  const api = useMemo(
    () =>
      new ApiClient({
        baseUrl: apiBaseUrl,
        getToken,
        onAuthFailure: handleAuthFailure,
        isGuest: () => isGuest,
        onGuestEnded: handleGuestEnded,
      }),
    [apiBaseUrl, getToken, handleAuthFailure, handleGuestEnded, isGuest],
  );
  return (
    <QueryClientProvider client={queryClient}>
      <ApiContext.Provider value={api}>
        <Workspace {...(title !== undefined && { title })} {...(info !== undefined && { info })} {...(providers !== undefined && { providers })} />
      </ApiContext.Provider>
    </QueryClientProvider>
  );
}

function AuthGate({ config }: { config: AppConfig }) {
  const { status } = useAuth();
  if (status === 'loading') return <p className="center-note" role="status">Signing in...</p>;
  if (status === 'anonymous') {
    return <LoginPage providers={config.auth.providers} branding={config.branding} guest={config.auth.guest} info={config.info} />;
  }
  return <Authenticated apiBaseUrl={config.apiBaseUrl} title={config.branding.title} info={config.info} providers={config.auth.providers} />;
}

export function App({ config }: { config: AppConfig }) {
  useEffect(() => {
    document.title = config.branding.title;
  }, [config.branding.title]);
  return (
    <AuthProvider auth={config.auth} apiBaseUrl={config.apiBaseUrl}>
      <AuthGate config={config} />
    </AuthProvider>
  );
}

/** Loads /config.json first; nothing auth-related is known at build time. */
export function Bootstrap() {
  const [config, setConfig] = useState<AppConfig | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    loadConfig()
      .then((c) => {
        if (!cancelled) setConfig(c);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load configuration');
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (error) {
    return (
      <p className="center-note" role="alert">
        The application is not configured: {error}
      </p>
    );
  }
  if (!config) return <p className="center-note" role="status">Loading...</p>;
  return <App config={config} />;
}
