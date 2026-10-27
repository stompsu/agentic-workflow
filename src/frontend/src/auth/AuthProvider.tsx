import { lazy, Suspense, type ReactNode } from 'react';
import type { AuthConfig } from '../config/config';
import { GuestLayer } from './GuestLayer';
import { MsalAuthProvider } from './MsalAuthProvider';

// `__E2E_MOCK_AUTH__` is replaced at compile time; in production builds this folds to `null` and the mock
// module (and its marker string) is not part of the bundle.
const MockAuthProvider = __E2E_MOCK_AUTH__ ? lazy(() => import('./mockAuth')) : null;

/**
 * Identity provider (MSAL, or the e2e mock) with guest mode layered on top. Both providers publish the social
 * identity; GuestLayer combines it with an optional guest token into the AuthContext the app uses.
 */
export function AuthProvider({ auth, apiBaseUrl = '/api', children }: { auth: AuthConfig; apiBaseUrl?: string; children: ReactNode }) {
  const layered = (
    <GuestLayer guestEnabled={auth.guest.enabled} apiBaseUrl={apiBaseUrl}>
      {children}
    </GuestLayer>
  );
  if (MockAuthProvider) {
    return (
      <Suspense fallback={null}>
        <MockAuthProvider>{layered}</MockAuthProvider>
      </Suspense>
    );
  }
  return <MsalAuthProvider auth={auth}>{layered}</MsalAuthProvider>;
}
