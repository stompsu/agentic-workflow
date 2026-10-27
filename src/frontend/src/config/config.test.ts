import { describe, expect, it } from 'vitest';
import { ConfigError, loadConfig, parseConfig } from './config';
import { json } from '../test/helpers';

const valid = {
  auth: {
    clientId: 'spa-id',
    authority: 'https://contoso.ciamlogin.com/',
    apiScopes: ['api://api-id/access_as_user'],
    providers: [{ id: 'google', label: 'Continue with Google', domainHint: 'google' }],
  },
};

describe('config', () => {
  it('parses the documented shape and applies defaults', () => {
    const c = parseConfig(valid);
    expect(c.apiBaseUrl).toBe('/api');
    expect(c.branding).toEqual({ title: 'Assistant', tagline: 'Sign in to continue.' });
    expect(c.auth.redirectUri).toBe('/redirect.html');
    expect(c.auth.knownAuthorities).toEqual(['contoso.ciamlogin.com']);
    expect(c.auth.providers[0]).toEqual({ id: 'google', label: 'Continue with Google', domainHint: 'google' });
  });

  it('reads optional app.title and app.tagline', () => {
    expect(parseConfig({ ...valid, app: { title: ' Acme ', tagline: 'Hi' } }).branding).toEqual({ title: 'Acme', tagline: 'Hi' });
  });

  it('keeps explicit values and ignores unknown properties', () => {
    const c = parseConfig({ ...valid, apiBaseUrl: '/x/', extra: 1, auth: { ...valid.auth, knownAuthorities: ['a', 'b'], redirectUri: '/' } });
    expect(c.apiBaseUrl).toBe('/x');
    expect(c.auth.knownAuthorities).toEqual(['a', 'b']);
    expect(c.auth.redirectUri).toBe('/');
  });

  it.each([null, {}, { auth: {} }, { auth: { ...valid.auth, clientId: '' } }, { auth: { ...valid.auth, apiScopes: [] } }])('rejects invalid config %#', (bad) => {
    expect(() => parseConfig(bad)).toThrow(ConfigError);
  });

  it('loadConfig fetches /config.json with no-store', async () => {
    let seen: RequestInit | undefined;
    const fetchImpl = (async (_u: RequestInfo | URL, init?: RequestInit) => {
      seen = init;
      return json(valid);
    }) as typeof fetch;
    const c = await loadConfig(fetchImpl);
    expect(c.auth.clientId).toBe('spa-id');
    expect(seen?.cache).toBe('no-store');
  });

  it('loadConfig reports HTTP and network failures', async () => {
    await expect(loadConfig((async () => new Response('', { status: 404 })) as typeof fetch)).rejects.toThrow(/404/);
    await expect(loadConfig((async () => { throw new TypeError('x'); }) as typeof fetch)).rejects.toThrow(ConfigError);
  });

  it('reads optional app.retentionDays and app.supportContact (plain text, trimmed)', () => {
    expect(parseConfig(valid).info).toEqual({});
    expect(parseConfig({ ...valid, app: { retentionDays: 90, supportContact: ' help@example.com ' } }).info).toEqual({
      retentionDays: 90,
      supportContact: 'help@example.com',
    });
    expect(parseConfig({ ...valid, app: { supportContact: '  ' } }).info).toEqual({});
    expect(parseConfig({ ...valid, app: { supportContact: '<b>x</b>' } }).info.supportContact).toBe('<b>x</b>');
  });

  it.each([{ retentionDays: '90' }, { retentionDays: 0 }, { retentionDays: 3651 }, { retentionDays: 1.5 }, { supportContact: 5 }])(
    'rejects invalid app info %#',
    (app) => {
      expect(() => parseConfig({ ...valid, app })).toThrow(ConfigError);
    },
  );

  it('guest access: disabled by default, label default, hours optional', () => {
    const c = parseConfig(valid);
    expect(c.auth.guest).toEqual({ enabled: false, label: 'Continue as guest' });
    expect(c.info.guestSessionHours).toBeUndefined();
    const g = parseConfig({
      ...valid,
      auth: { ...valid.auth, guest: { enabled: true, label: ' Try it ' } },
      app: { guestSessionHours: 4, guestRetentionHours: 5 },
    });
    expect(g.auth.guest).toEqual({ enabled: true, label: 'Try it' });
    expect(g.info).toEqual({ guestSessionHours: 4, guestRetentionHours: 5 });
  });

  it.each([
    { auth: { guest: { enabled: 'true' } } },
    { auth: { guest: 'yes' } },
    { app: { guestSessionHours: 0 } },
    { app: { guestSessionHours: 13 } },
    { app: { guestRetentionHours: 169 } },
    { app: { guestRetentionHours: '5' } },
    { app: { guestRetentionHours: 1.5 } },
  ])('rejects invalid guest config %#', (over) => {
    expect(() => parseConfig({ ...valid, ...('app' in over ? { app: over.app } : {}), auth: { ...valid.auth, ...('auth' in over ? over.auth : {}) } })).toThrow(ConfigError);
  });
});
