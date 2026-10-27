import { lazy, Suspense, type ReactNode } from 'react';
import type { AuthConfig } from '../config/config';
import { MsalAuthProvider } from './MsalAuthProvider';

// `__E2E_MOCK_AUTH__` and `__LOCAL_AUTH__` are replaced at compile time; in production builds both fold to `null`
// and the test/local modules (and their marker strings) are not part of the bundle. Precedence: e2e mock > local > MSAL.
const MockAuthProvider = __E2E_MOCK_AUTH__ ? lazy(() => import('./mockAuth')) : null;
const LocalAuthProvider = !__E2E_MOCK_AUTH__ && __LOCAL_AUTH__ ? lazy(() => import('./localAuth')) : null;

export function AuthProvider({ auth, children }: { auth: AuthConfig; children: ReactNode }) {
  if (MockAuthProvider) {
    return (
      <Suspense fallback={null}>
        <MockAuthProvider>{children}</MockAuthProvider>
      </Suspense>
    );
  }
  if (LocalAuthProvider) {
    return (
      <Suspense fallback={null}>
        <LocalAuthProvider>{children}</LocalAuthProvider>
      </Suspense>
    );
  }
  return <MsalAuthProvider auth={auth}>{children}</MsalAuthProvider>;
}
