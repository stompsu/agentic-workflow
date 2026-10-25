// MSAL Browser v5 redirect bridge page entry (served as /redirect.html, same origin, never from a CDN).
import { broadcastResponseToMainFrame } from '@azure/msal-browser/redirect-bridge';

broadcastResponseToMainFrame().catch((error: unknown) => {
  console.error('Error broadcasting auth response', error);
});
