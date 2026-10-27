# Support assistant frontend

Vite + React + TypeScript (strict). Node >= 24 (`.nvmrc`).

| Script | What |
|---|---|
| `npm run dev` | Vite dev server on :5173, `/api` proxied to `http://localhost:5080` (override with `BACKEND_URL`) |
| `npm run lint` / `typecheck` / `test` | ESLint / `tsc --noEmit` / `vitest run` |
| `npm run build` | `tsc` + `vite build` + `scripts/check-dist.mjs` (fails if e2e mock-auth code is in `dist/`) |
| `npm run test:entrypoint` | shell test of the container entrypoint (escaping, validation); needs `jq` |
| `npm run e2e` | Playwright happy path + delete-my-data (needs browsers: `npx playwright install chromium`, not run automatically) |

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
| `APP_RETENTION_DAYS` | no | integer 1..3650, same value as backend `Chat:RetentionDays`; `app.retentionDays` (sidebar note "Chats are kept for N days after last activity") and the retention text on `/data-deletion.html`. Invalid value: container fails to start |
| `APP_SUPPORT_CONTACT` | no (prod: yes) | plain text, max 200 chars, no control characters (e.g. an email address); `app.supportContact` (shown in the "Delete my data" result notice) and `/data-deletion.html`. Always rendered as text, HTML-escaped |
| `AUTH_GUEST_ENABLED` | no | `true` or `false` (default `false`); shows "Continue as guest" on the login page (`auth.guest.enabled`). Must match backend `Guest__Enabled`. Anything else: container fails to start |
| `AUTH_GUEST_LABEL` | no | button label, 1..60 chars plain text (default "Continue as guest"); `auth.guest.label` |
| `APP_GUEST_SESSION_HOURS` | no | integer 1..12, same as backend `Guest:MaxLifetime` in hours (login caption, default 4); `app.guestSessionHours` |
| `APP_GUEST_RETENTION_HOURS` | no | integer 1..168, `Guest:DataTtl` rounded up to hours (caption and sidebar note, default 5); `app.guestRetentionHours` |
| `API_BASE_URL` | no | default `/api` |

None are secrets.

`/data-deletion.html` (+ `data-deletion.css`) is a static public page (Facebook "Data deletion instructions URL",
docs/auth-setup.md 4.1). `public/data-deletion.html` holds the placeholders `__APP_TITLE__`, `__RETENTION_TEXT__`,
`__SUPPORT_CONTACT_TEXT__`; the entrypoint replaces them in place at start with HTML-escaped values (jq, single pass).
Under `npm run dev` the placeholders stay visible. `npm run test:entrypoint` checks the entrypoint (escaping, no
placeholder left, validation); needs `jq`.

## Delete my data

Account menu -> "Delete my data" -> confirmation dialog -> `DELETE /api/me/data` (api-contract 2.8): the in-flight stream
is aborted first, `503 deletion_incomplete` is repeated after `Retry-After` (5 attempts in total), `204` clears the React
Query cache, shows the new-chat view and a notice (plus `app.supportContact` when configured). The user stays signed in.

## E2E mock auth (test-only)

`vite --mode e2e` (used only by `playwright.config.ts`) defines the compile-time constant `__E2E_MOCK_AUTH__ = true`,
which swaps MSAL for `src/auth/mockAuth.tsx`. It cannot reach production: `vite build --mode e2e` throws, in normal
builds the constant is `false` so the mock module is dead code and not bundled, and `scripts/check-dist.mjs`
(part of `npm run build` and the Docker build) fails if the marker string `E2E_MOCK_AUTH_ACTIVE` appears in `dist/`.
