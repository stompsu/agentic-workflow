export interface ProviderConfig {
  id: string;
  label: string;
  /** Passed as `domain_hint` (Entra External ID issuer acceleration). Absent = hosted provider chooser. */
  domainHint?: string;
}

export interface AuthConfig {
  clientId: string;
  authority: string;
  knownAuthorities: string[];
  redirectUri: string;
  apiScopes: string[];
  providers: ProviderConfig[];
}

export interface BrandingConfig {
  /** Application name shown on the login page, the top bar and the document title. */
  title: string;
  /** One-line text under the title on the login page. */
  tagline: string;
}

export const DEFAULT_BRANDING: BrandingConfig = { title: 'Assistant', tagline: 'Sign in to continue.' };

export interface AppConfig {
  auth: AuthConfig;
  branding: BrandingConfig;
  apiBaseUrl: string;
}

export class ConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ConfigError';
  }
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function str(v: unknown, name: string): string {
  if (typeof v !== 'string' || v.trim() === '') throw new ConfigError(`config.json: "${name}" must be a non-empty string`);
  return v;
}

function strArray(v: unknown, name: string): string[] {
  if (!Array.isArray(v) || v.some((x) => typeof x !== 'string' || x === '')) {
    throw new ConfigError(`config.json: "${name}" must be an array of non-empty strings`);
  }
  return v as string[];
}

/** Validates and normalizes the runtime config (docs/auth-setup.md section 7). Unknown properties are ignored. */
export function parseConfig(raw: unknown): AppConfig {
  if (!isRecord(raw)) throw new ConfigError('config.json: expected a JSON object');
  const auth = raw.auth;
  if (!isRecord(auth)) throw new ConfigError('config.json: "auth" is missing');

  const authority = str(auth.authority, 'auth.authority');
  const apiScopes = strArray(auth.apiScopes, 'auth.apiScopes');
  if (apiScopes.length === 0) throw new ConfigError('config.json: "auth.apiScopes" must not be empty');

  let knownAuthorities: string[];
  if (auth.knownAuthorities === undefined) {
    try {
      knownAuthorities = [new URL(authority).host];
    } catch {
      throw new ConfigError('config.json: "auth.authority" is not a valid URL');
    }
  } else {
    knownAuthorities = strArray(auth.knownAuthorities, 'auth.knownAuthorities');
  }

  const providers: ProviderConfig[] = [];
  if (auth.providers !== undefined) {
    if (!Array.isArray(auth.providers)) throw new ConfigError('config.json: "auth.providers" must be an array');
    for (const p of auth.providers as unknown[]) {
      if (!isRecord(p)) throw new ConfigError('config.json: invalid provider entry');
      const provider: ProviderConfig = { id: str(p.id, 'provider.id'), label: str(p.label, 'provider.label') };
      if (typeof p.domainHint === 'string' && p.domainHint !== '') provider.domainHint = p.domainHint;
      providers.push(provider);
    }
  }

  const apiBaseUrl = typeof raw.apiBaseUrl === 'string' && raw.apiBaseUrl !== '' ? raw.apiBaseUrl.replace(/\/+$/, '') : '/api';

  const app = isRecord(raw.app) ? raw.app : {};
  const nonEmpty = (v: unknown, fallback: string) => (typeof v === 'string' && v.trim() !== '' ? v.trim() : fallback);

  return {
    branding: { title: nonEmpty(app.title, DEFAULT_BRANDING.title), tagline: nonEmpty(app.tagline, DEFAULT_BRANDING.tagline) },
    auth: {
      clientId: str(auth.clientId, 'auth.clientId'),
      authority,
      knownAuthorities,
      redirectUri: typeof auth.redirectUri === 'string' && auth.redirectUri !== '' ? auth.redirectUri : '/redirect.html',
      apiScopes,
      providers,
    },
    apiBaseUrl,
  };
}

/** Fetches /config.json at startup (served no-store). Nothing auth-related is baked at build time. */
export async function loadConfig(fetchImpl: typeof fetch = fetch, url = '/config.json'): Promise<AppConfig> {
  let response: Response;
  try {
    response = await fetchImpl(url, { cache: 'no-store', headers: { Accept: 'application/json' } });
  } catch {
    throw new ConfigError('Could not load /config.json');
  }
  if (!response.ok) throw new ConfigError(`Could not load /config.json (HTTP ${response.status})`);
  let json: unknown;
  try {
    json = await response.json();
  } catch {
    throw new ConfigError('/config.json is not valid JSON');
  }
  return parseConfig(json);
}
