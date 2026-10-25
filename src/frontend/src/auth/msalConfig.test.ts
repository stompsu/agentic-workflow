import { describe, expect, it } from 'vitest';
import { buildLoginRequest, buildMsalConfiguration, resolveRedirectUri } from './msalConfig';
import { parseConfig } from '../config/config';

const auth = parseConfig({
  auth: { clientId: 'c', authority: 'https://t.ciamlogin.com/', apiScopes: ['api://x/access_as_user'], redirectUri: '/redirect.html' },
}).auth;

describe('msal configuration', () => {
  it('uses the CIAM authority, knownAuthorities, resolved redirect URI and sessionStorage', () => {
    const c = buildMsalConfiguration(auth, 'https://app.example.com');
    expect(c.auth?.authority).toBe('https://t.ciamlogin.com/');
    expect(c.auth?.knownAuthorities).toEqual(['t.ciamlogin.com']);
    expect(c.auth?.redirectUri).toBe('https://app.example.com/redirect.html');
    expect(c.auth?.postLogoutRedirectUri).toBe('https://app.example.com/');
    expect(c.cache?.cacheLocation).toBe('sessionStorage');
  });

  it('resolves a relative redirect URI against the origin', () => {
    expect(resolveRedirectUri('/', 'http://localhost:5173')).toBe('http://localhost:5173/');
  });

  it('passes domain_hint per provider and requests the API scope', () => {
    expect(buildLoginRequest(['s'], 'google')).toEqual({ scopes: ['s'], extraQueryParameters: { domain_hint: 'google' } });
    expect(buildLoginRequest(['s'])).toEqual({ scopes: ['s'] });
  });
});
