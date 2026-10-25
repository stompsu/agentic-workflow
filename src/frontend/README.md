# Support assistant frontend

Vite + React + TypeScript (strict). Node >= 24 (`.nvmrc`).

| Script | What |
|---|---|
| `npm run dev` | Vite dev server on :5173, `/api` proxied to `http://localhost:5080` (override with `BACKEND_URL`) |
| `npm run lint` / `typecheck` / `test` | ESLint / `tsc --noEmit` / `vitest run` |
| `npm run build` | `tsc` + `vite build` + `scripts/check-dist.mjs` (fails if e2e mock-auth code is in `dist/`) |
| `npm run e2e` | Playwright happy path (needs browsers: `npx playwright install chromium`, not run automatically) |

## Runtime config

`GET /config.json` is fetched at startup (shape: docs/auth-setup.md section 7, plus `auth.knownAuthorities`).
Local: `public/config.json` (placeholders only; put your dev tenant values there, do not commit real ones if you prefer).
Container: written at start by `docker-entrypoint.d/10-write-config.sh` from env vars:

| Env var | Required | Meaning |
|---|---|---|
| `BACKEND_UPSTREAM` | yes | backend internal FQDN, host only (nginx `proxy_pass`, Host header, SNI) |
| `AUTH_CLIENT_ID` | yes | SPA client id |
| `AUTH_AUTHORITY` | yes | `https://<sub>.ciamlogin.com/` |
| `AUTH_API_SCOPE` | yes | `api://<api-id>/access_as_user` (comma separated for several) |
| `AUTH_PROVIDERS` | no | JSON array `[{id,label,domainHint}]`; default Google + Facebook |
| `AUTH_KNOWN_AUTHORITIES` | no | comma separated hosts; default host of `AUTH_AUTHORITY` |
| `AUTH_REDIRECT_URI` | no | default `/redirect.html` (MSAL v5 redirect bridge; register `<origin>/redirect.html`) |
| `APP_TITLE` | no | `app.title`: application name on the login page, top bar and document title; default `Assistant` |
| `APP_TAGLINE` | no | `app.tagline`: line under the title on the login page; default `Sign in to continue.` |
| `API_BASE_URL` | no | default `/api` |

None are secrets.

## E2E mock auth (test-only)

`vite --mode e2e` (used only by `playwright.config.ts`) defines the compile-time constant `__E2E_MOCK_AUTH__ = true`,
which swaps MSAL for `src/auth/mockAuth.tsx`. It cannot reach production: `vite build --mode e2e` throws, in normal
builds the constant is `false` so the mock module is dead code and not bundled, and `scripts/check-dist.mjs`
(part of `npm run build` and the Docker build) fails if the marker string `E2E_MOCK_AUTH_ACTIVE` appears in `dist/`.
