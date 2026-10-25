// E2E-ONLY auth bypass. Imported solely behind the compile-time constant __E2E_MOCK_AUTH__ (true only for
// `vite --mode e2e`, never for `vite build`). scripts/check-dist.mjs fails the build if the marker below
// ends up in dist/.
import { useMemo, useState, type ReactNode } from 'react';
import { AuthContext, type AuthContextValue } from './AuthContext';

export const E2E_MOCK_AUTH_MARKER = 'E2E_MOCK_AUTH_ACTIVE';

export default function MockAuthProvider({ children }: { children: ReactNode }) {
  const [signedIn, setSignedIn] = useState(false);
  const value = useMemo<AuthContextValue>(
    () => ({
      status: signedIn ? 'authenticated' : 'anonymous',
      userName: signedIn ? 'Test User' : null,
      sessionExpired: false,
      login: async () => setSignedIn(true),
      logout: async () => setSignedIn(false),
      getToken: async () => 'e2e-test-token',
      handleAuthFailure: () => setSignedIn(false),
    }),
    [signedIn],
  );
  return (
    <AuthContext.Provider value={value}>
      <div hidden data-testid={E2E_MOCK_AUTH_MARKER} />
      {children}
    </AuthContext.Provider>
  );
}
