import { BrowserCacheLocation, type Configuration } from '@azure/msal-browser';
import type { AuthConfig } from '../config/config';

export function resolveRedirectUri(redirectUri: string, origin: string = window.location.origin): string {
  return new URL(redirectUri, origin).href;
}

/**
 * MSAL configuration for Entra External ID (CIAM): authority https://<sub>.ciamlogin.com/, knownAuthorities lists
 * the authority host (and the tenant-GUID issuer host if the tenant returns one), authorization code + PKCE,
 * token cache in sessionStorage (never localStorage).
 */
export function buildMsalConfiguration(auth: AuthConfig, origin: string = window.location.origin): Configuration {
  return {
    auth: {
      clientId: auth.clientId,
      authority: auth.authority,
      knownAuthorities: auth.knownAuthorities,
      redirectUri: resolveRedirectUri(auth.redirectUri, origin),
      postLogoutRedirectUri: new URL('/', origin).href,
    },
    cache: { cacheLocation: BrowserCacheLocation.SessionStorage },
  };
}

export function buildLoginRequest(scopes: string[], domainHint?: string) {
  return {
    scopes,
    ...(domainHint ? { extraQueryParameters: { domain_hint: domainHint } } : {}),
  };
}
