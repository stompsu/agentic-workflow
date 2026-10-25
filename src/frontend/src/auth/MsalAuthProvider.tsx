import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  InteractionRequiredAuthError,
  InteractionStatus,
  createStandardPublicClientApplication,
  type IPublicClientApplication,
} from '@azure/msal-browser';
import { MsalProvider, useMsal } from '@azure/msal-react';
import type { AuthConfig } from '../config/config';
import { AuthContext, type AuthContextValue } from './AuthContext';
import { buildLoginRequest, buildMsalConfiguration } from './msalConfig';

function MsalBridge({ auth, children }: { auth: AuthConfig; children: ReactNode }) {
  const { instance, accounts, inProgress } = useMsal();
  const [expired, setExpired] = useState(false);
  const account = accounts[0];

  const getToken = useCallback(
    async (forceRefresh = false): Promise<string> => {
      const acct = instance.getActiveAccount() ?? instance.getAllAccounts()[0];
      if (!acct) throw new Error('Not signed in');
      try {
        const result = await instance.acquireTokenSilent({ scopes: auth.apiScopes, account: acct, forceRefresh });
        return result.accessToken;
      } catch (e) {
        if (e instanceof InteractionRequiredAuthError) {
          await instance.acquireTokenRedirect({ scopes: auth.apiScopes, account: acct });
          return new Promise<string>(() => undefined); // the page is navigating away
        }
        throw e;
      }
    },
    [instance, auth.apiScopes],
  );

  const handleAuthFailure = useCallback(() => setExpired(true), []);

  const value = useMemo<AuthContextValue>(() => {
    const loading = inProgress === InteractionStatus.Startup || inProgress === InteractionStatus.HandleRedirect;
    const authenticated = account !== undefined && !expired;
    return {
      status: loading ? 'loading' : authenticated ? 'authenticated' : 'anonymous',
      userName: account?.name ?? account?.username ?? null,
      sessionExpired: expired,
      login: async (domainHint) => {
        setExpired(false);
        await instance.loginRedirect(buildLoginRequest(auth.apiScopes, domainHint));
      },
      logout: async () => {
        await instance.logoutRedirect(account ? { account } : undefined);
      },
      getToken,
      handleAuthFailure,
    };
  }, [account, auth.apiScopes, expired, getToken, handleAuthFailure, inProgress, instance]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

/** Real auth: MSAL (authorization code + PKCE) against Entra External ID, configured from runtime config. */
export function MsalAuthProvider({ auth, children }: { auth: AuthConfig; children: ReactNode }) {
  const [instance, setInstance] = useState<IPublicClientApplication | null>(null);
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    let cancelled = false;
    createStandardPublicClientApplication(buildMsalConfiguration(auth))
      .then((pca) => {
        if (!cancelled) {
          const first = pca.getAllAccounts()[0];
          if (first) pca.setActiveAccount(first);
          setInstance(pca);
        }
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e : new Error('Authentication failed to initialize'));
      });
    return () => {
      cancelled = true;
    };
  }, [auth]);

  if (error) throw error;
  if (!instance) return <p className="center-note" role="status">Loading...</p>;
  return (
    <MsalProvider instance={instance}>
      <MsalBridge auth={auth}>{children}</MsalBridge>
    </MsalProvider>
  );
}
