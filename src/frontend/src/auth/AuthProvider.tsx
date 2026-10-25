import { lazy, Suspense, type ReactNode } from 'react';
import type { AuthConfig } from '../config/config';
import { MsalAuthProvider } from './MsalAuthProvider';

// `__E2E_MOCK_AUTH__` is replaced at compile time; in production builds this folds to `null` and the mock
// module (and its marker string) is not part of the bundle.
const MockAuthProvider = __E2E_MOCK_AUTH__ ? lazy(() => import('./mockAuth')) : null;

export function AuthProvider({ auth, children }: { auth: AuthConfig; children: ReactNode }) {
  if (MockAuthProvider) {
    return (
      <Suspense fallback={null}>
        <MockAuthProvider>{children}</MockAuthProvider>
      </Suspense>
    );
  }
  return <MsalAuthProvider auth={auth}>{children}</MsalAuthProvider>;
}
