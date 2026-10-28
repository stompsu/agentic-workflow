# Auth setup: Entra External ID with Google and Facebook

Manual portal steps, run by the user. Verified against Microsoft Learn (External ID pages updated 2026-09; user flow
page re-checked 2026-10-25). Nothing here is automated by Bicep or pipelines. Placeholders: `<sub>` = tenant subdomain
(from `<sub>.onmicrosoft.com`), `<tid>` = Directory (tenant) ID GUID, `<spa-id>`, `<api-id>` = application (client) IDs.
Decisions applied (2026-10-25, user): D4 two tenants, D6 social only in prod + email one-time passcode in nonprod,
D7 data deletion page + account deletion procedure, D10 no custom domain (ciamlogin.com for auth).

## 0. Tenant topology (decided, D4)

Two external tenants:
- `nonprod` tenant: shared by dev and test, with separate app registrations per environment.
- `prod` tenant: production only.

Why not one tenant for all: prod customer identities, branding, consent and social-provider apps should not be
shared with environments where developers have admin rights and test accounts. Why not three: dev and test share the
same risk profile; a third tenant doubles Google/Facebook app setup and Facebook review effort for no isolation benefit.
Effect: `oid` values differ between nonprod and prod (expected; data never moves between them).

| Tenant | Sign-in methods (D6) | Social apps |
|---|---|---|
| nonprod | Google, Facebook, email one-time passcode (for test users) | Google project in testing mode; Meta app in development mode |
| prod | Google, Facebook only | Google consent screen published; Meta app Live |

## 1. Create the external tenant (per tenant)

1. Microsoft Entra admin center (https://entra.microsoft.com) -> create an external tenant
   ("Create a new external tenant in the Microsoft Entra admin center"), or use the External ID VS Code extension.
2. Record from the tenant overview: tenant subdomain `<sub>` and tenant ID `<tid>`.
3. Switch to the tenant (Settings icon -> Directories + subscriptions) for all following steps.

## 2. App registrations (per environment)

In the tenant: Entra ID -> App registrations -> New registration. Supported account types:
"Accounts in this organizational directory only". One API + one SPA registration per environment:

| Env | Tenant | API app | SPA app |
|---|---|---|---|
| dev | nonprod | `agentwf-api-dev` | `agentwf-spa-dev` |
| test | nonprod | `agentwf-api-test` | `agentwf-spa-test` |
| prod | prod | `agentwf-api-prod` | `agentwf-spa-prod` |

### 2.1 API app
1. Register (no redirect URI). Record `<api-id>`.
2. Expose an API -> Application ID URI -> Add -> accept `api://<api-id>` -> Save.
3. Add a scope: name `access_as_user`, Who can consent: Admins only, display names/descriptions
   "Access Agentic Workflow as the signed-in user", State Enabled.
4. Manifest: set `requestedAccessTokenVersion` (in `api`) to `2` if it is not already 2. Save.
5. No client secret, no app roles (the API only validates tokens).

### 2.2 SPA app
1. Register with platform "Single-page application" and the redirect URIs from section 6. Record `<spa-id>`.
2. Authentication: no implicit grant (leave access/ID token checkboxes off; MSAL uses auth code + PKCE).
   Front-channel logout URL optional.
3. API permissions -> Add a permission -> APIs my organization uses -> select the API app of the SAME environment ->
   Delegated -> `access_as_user` -> Add.
4. Grant admin consent for the tenant (customers are not prompted to consent to the API scope).

## 3. Google identity provider (per tenant)

Google Cloud console (https://console.cloud.google.com), one Google project per tenant:
1. New Project -> select it.
2. APIs & services -> OAuth consent screen -> User type External -> Create. App name, support email.
   Authorized domains: add `ciamlogin.com` and `microsoftonline.com`. Developer contact emails. Save.
3. Credentials -> Create credentials -> OAuth client ID -> Web application, name e.g. "Microsoft Entra External ID".
4. Authorized redirect URIs (replace `<tid>` and `<sub>`; exactly as on Learn):
   ```
   https://login.microsoftonline.com
   https://login.microsoftonline.com/te/<tid>/oauth2/authresp
   https://login.microsoftonline.com/te/<sub>.onmicrosoft.com/oauth2/authresp
   https://<tid>.ciamlogin.com/<tid>/federation/oidc/accounts.google.com
   https://<tid>.ciamlogin.com/<sub>.onmicrosoft.com/federation/oidc/accounts.google.com
   https://<sub>.ciamlogin.com/<tid>/federation/oauth2
   https://<sub>.ciamlogin.com/<sub>.onmicrosoft.com/federation/oauth2
   ```
5. Create; record Client ID and Client secret. Publish the consent screen (prod) / keep testing mode with test users (nonprod).
   Google may require app verification (e.g. after adding a logo).

Entra admin center (in the tenant): Entra ID -> External Identities -> All identity providers -> Built-in tab ->
Google -> Configure: Name `Google`, Client ID, Client secret -> Save.
The secret lives only in Entra; do not store it in the repo, Key Vault or pipeline variables.

## 4. Facebook identity provider (per tenant)

Facebook for developers (https://developers.facebook.com/apps), one Meta app per tenant:
1. Create App -> "Authenticate and request data from users with Facebook Login" -> not a game -> name + contact email -> Create.
2. App settings -> Basic: copy App ID and App Secret. Set Privacy Policy URL, Terms of Service URL (real pages you
   host), Category, and the User data deletion setting from section 4.1 (required for Live mode). Add platform ->
   Website -> Site URL (frontend URL). Save.
3. Use cases -> Authentication and account creation -> Customize -> Facebook Login settings ->
   Valid OAuth Redirect URIs (Learn uses `<tenant-name>` = `<sub>`):
   ```
   https://login.microsoftonline.com/te/<tid>/oauth2/authresp
   https://login.microsoftonline.com/te/<sub>.onmicrosoft.com/oauth2/authresp
   https://<sub>.ciamlogin.com/<tid>/federation/oidc/www.facebook.com
   https://<sub>.ciamlogin.com/<sub>.onmicrosoft.com/federation/oidc/www.facebook.com
   https://<sub>.ciamlogin.com/<tid>/federation/oauth2
   https://<sub>.ciamlogin.com/<sub>.onmicrosoft.com/federation/oauth2
   ```
   Save.
4. Use cases -> Authentication and account creation -> Permissions -> add `email`.
5. Nonprod: keep the app in development mode; only app roles/testers can sign in (add testers). Test users without a
   Facebook test role use email one-time passcode instead (section 5.1).
   Prod: Go live (data handling questions, business verification; plan lead time).

Entra admin center: External Identities -> All identity providers -> Built-in -> Facebook -> Configure:
Name `Facebook`, Client ID = App ID, Client secret = App Secret -> Save.

### 4.1 Data deletion instructions URL (D7; required before Go live)

We use Meta's "Data deletion instructions URL" option, not a data deletion callback: users delete their chat data
themselves in the app ("Delete my data", api-contract 2.8), and the page explains how.

Facebook developer console, prod Meta app (optional in the nonprod app, which stays in development mode):
1. App settings -> Basic -> "User data deletion" -> choose "Data deletion instructions URL".
   (Meta has moved this field before; if it is not under Basic, look under App settings -> Advanced.)
2. URL: `https://<prod-frontend-fqdn>/data-deletion.html` (D10: no custom domain; use the Container App FQDN from the
   azure-infra outputs). Save.
3. Open the URL in a private browser window and confirm it loads without sign-in before submitting for review.

The page (owner frontend-react):
- Static HTML at `/data-deletion.html`, public (no auth), no JavaScript, no external resources. Committed as
  `src/frontend/public/data-deletion.html` with the placeholders `__APP_TITLE__`, `__RETENTION_TEXT__` and
  `__SUPPORT_CONTACT_TEXT__`; the container entrypoint (`docker-entrypoint.d/10-write-config.sh`) replaces them in
  place at start with HTML-escaped values from `APP_TITLE`, `APP_RETENTION_DAYS` and `APP_SUPPORT_CONTACT`
  (section 7), writing to a temp file and renaming, exactly like `config.json`. Under the Vite dev server the
  placeholders stay visible (acceptable, like the placeholder `config.json`).
- nginx: `location = /data-deletion.html` with the normal security headers and `Cache-Control: no-cache`.
- Required content:
  1. What is stored: chat conversations (sessions and messages) linked to the account you signed in with.
  2. How to delete it: open this site (link to `/`), sign in with the SAME provider you used before (Google or
     Facebook; each provider is a separate account with its own history), open the account menu, choose
     "Delete my data" and confirm. All chat history is deleted immediately.
  3. `__RETENTION_TEXT__`. With `APP_RETENTION_DAYS = N`: "Chat data is also deleted automatically: each message N
     days after it was sent, and each conversation N days after its last activity." Without it: "Chat data is also
     deleted automatically after a retention period."
  4. The sign-in account is separate: "Delete my data" does not delete it. `__SUPPORT_CONTACT_TEXT__`. With
     `APP_SUPPORT_CONTACT = X`: "To have your sign-in account deleted as well, contact X." Without it: "To have your
     sign-in account deleted as well, contact the operator of this service." Prod MUST set `APP_SUPPORT_CONTACT`.
  5. Facebook side: you can also remove this app's access in Facebook (Settings and privacy -> Settings -> Apps and
     websites -> Remove); that only removes Facebook's permission, not the chat data.

## 5. User flow (per tenant) and app assignment

Common steps:
1. Entra ID -> External Identities -> User flows -> New user flow. Name `SignUpSignIn`.
2. Identity providers: per tenant, sections 5.1 / 5.2.
3. User attributes: Display Name (keep minimal; email comes from the provider when shared).
4. Create.
5. Open the user flow -> Applications -> Add application -> select the SPA app(s) of that tenant
   (`agentwf-spa-dev` and `agentwf-spa-test` in nonprod; `agentwf-spa-prod` in prod). An application can have
   only one user flow; one flow may serve several apps. The API app is not added.
6. Test: User flows -> SignUpSignIn -> Run user flow (or sign in from the SPA) with every enabled method.

### 5.1 nonprod tenant: Google, Facebook and email one-time passcode
1. Before creating the flow: External Identities -> All identity providers -> Email One-time-passcode -> make sure
   it is enabled for the tenant (Learn prerequisite for OTP sign-in).
2. In the user flow, under Identity providers: select the Email Accounts check box -> option
   "Email one-time passcode" (NOT "Email with password"); also select Google and Facebook.
3. If `SignUpSignIn` already exists without email: User flows -> SignUpSignIn -> Identity providers -> select Email
   Accounts -> "Email one-time passcode" -> Save. Learn: changing the local-account method affects new users only.
4. Test users sign up with any mailbox they control; no Google test-user or Facebook tester entry is needed for them.
   Their `oid` is just another nonprod user object (data isolation is unchanged).

### 5.2 prod tenant: Google and Facebook only
1. Leave the tenant-level Email One-time-passcode setting as it is; it has no effect when no user flow offers email
   accounts.
2. In the user flow, under Identity providers: leave Email Accounts UNCHECKED; select Google and Facebook only.
3. Check (section 8) that the hosted sign-in page in prod shows only Google and Facebook. If the portal refuses to
   create a flow without an email-account option, stop and report it to the architect (this would reopen D6).

Provider buttons in the SPA pass `domain_hint=google` / `domain_hint=facebook` (Learn "issuer acceleration").
Verify in the nonprod tenant that the hint skips the chooser; if not, the hosted page still shows the providers (Q6).

Identity note: the same person signing in once with Google and once with Facebook (or with email OTP in nonprod) gets
separate user objects by default, i.e. different `oid`s and separate chat histories (ADR 0001). No account linking in v1.

## 6. SPA redirect URIs (platform: Single-page application)

The SPA uses `@azure/msal-browser` v5 (5.26.0), which requires a redirect bridge page: `redirectUri` must point to a
page that calls `broadcastResponseToMainFrame()` from `@azure/msal-browser/redirect-bridge`, and the registered URI must
match exactly (path, protocol, port). Sources: MSAL.js
[redirect-bridge.md](https://github.com/AzureAD/microsoft-authentication-library-for-js/blob/dev/lib/msal-browser/docs/redirect-bridge.md)
and [v4-migration.md](https://github.com/AzureAD/microsoft-authentication-library-for-js/blob/dev/lib/msal-browser/docs/v4-migration.md).
Our bridge is `/redirect.html` (served by nginx with `Cache-Control: no-store` and no COOP header, as the docs require),
so the redirect URI is `<origin>/redirect.html`.

| Env | Redirect URIs |
|---|---|
| dev | `http://localhost:5173/redirect.html`, `https://<dev-frontend-fqdn>/redirect.html` |
| test | `https://<test-frontend-fqdn>/redirect.html` |
| prod | `https://<prod-frontend-fqdn>/redirect.html` |

`<env-frontend-fqdn>` = Container App FQDN from azure-infra outputs, e.g. `ca-agentwf-web-dev.<env-unique>.<region>.azurecontainerapps.io`.
No custom domain for now (D10): the frontend uses the Container App FQDN and auth stays on `<sub>.ciamlogin.com`
(Learn Q&A reports social providers may not work through External ID custom URL domains). Adding a frontend custom
domain later means adding its `/redirect.html` URI here and updating the Facebook Site URL and section 4.1 URL; the
authority does not change.
Post-logout redirect URI is `<origin>/` (the SPA uses `logoutRedirect`, for which the bridge is not needed).
Localhost is allowed only in the dev registration.

## 7. Resulting configuration

Frontend runtime config (`/config.json`, generated at container start from env vars):
```json
{
  "auth": {
    "clientId": "<spa-id>",
    "authority": "https://<sub>.ciamlogin.com/",
    "knownAuthorities": ["<sub>.ciamlogin.com"],
    "redirectUri": "/redirect.html",
    "apiScopes": ["api://<api-id>/access_as_user"],
    "providers": [
      { "id": "google",   "label": "Continue with Google",   "domainHint": "google" },
      { "id": "facebook", "label": "Continue with Facebook", "domainHint": "facebook" }
    ]
  },
  "apiBaseUrl": "/api",
  "app": { "title": "Assistant", "tagline": "Sign in to continue.", "supportContact": "support@example.com", "retentionDays": 30 }
}
```
`app.title` and `app.tagline` are optional display text (defaults shown); keep them workflow-neutral.
`app.supportContact` is optional (omitted when `APP_SUPPORT_CONTACT` is unset); the SPA shows it as plain text in the
"Delete my data" result notice (api-contract 2.8). Additive field; the SPA config parser must accept and validate it
as a string. `app.retentionDays` is optional too (integer 1..3650, omitted when `APP_RETENTION_DAYS` is unset); the
SPA shows "Chats are kept for N days after last activity." only when it is set.
`auth.knownAuthorities` is an array of hosts; if omitted it defaults to the host of `auth.authority`. If the tenant
issues tokens or redirects on the GUID host, add `<tid>.ciamlogin.com` as well (to be confirmed together with Q1).

Provider buttons and the email option (D6): the login page renders one button per `auth.providers` entry. An entry
WITH `domainHint` starts `loginRedirect` with `domain_hint` (straight to that provider, Q6). An entry WITHOUT
`domainHint` starts `loginRedirect` without a hint, i.e. the generic sign-in that opens the hosted chooser; the
generic "Sign in" button shown when `providers` is empty does the same. The hosted chooser lists whatever the tenant's
user flow offers, so in nonprod it includes email one-time passcode. Nonprod frontends (dev, test) therefore set:
```
AUTH_PROVIDERS=[{"id":"google","label":"Continue with Google","domainHint":"google"},{"id":"facebook","label":"Continue with Facebook","domainHint":"facebook"},{"id":"email","label":"Sign in with email"}]
```
Prod leaves `AUTH_PROVIDERS` unset (default Google + Facebook). No SPA code change is needed for this; azure-infra
sets the value per environment (Bicep param `authProviders`).

The repo commits `src/frontend/public/config.json` with placeholders only (`CHANGE-ME`, zero GUIDs) for local dev.
Real values come only from the container env vars below at start. There is no local override file; developers edit the
placeholders locally and must not commit real values.

Frontend container env vars (none are secrets):

| Variable | Required | Value / default |
|---|---|---|
| `BACKEND_UPSTREAM` | yes | backend internal FQDN, bare host (no scheme or port); nginx `proxy_pass`, `Host`, SNI |
| `AUTH_CLIENT_ID` | yes | `<spa-id>` |
| `AUTH_AUTHORITY` | yes | `https://<sub>.ciamlogin.com/` |
| `AUTH_API_SCOPE` | yes | `api://<api-id>/access_as_user` (comma-separated for several) |
| `AUTH_PROVIDERS` | no | JSON array `[{id,label,domainHint?}]`; default Google + Facebook; nonprod adds the email entry above |
| `AUTH_KNOWN_AUTHORITIES` | no | comma-separated hosts; default host of `AUTH_AUTHORITY` |
| `AUTH_REDIRECT_URI` | no | default `/redirect.html` |
| `API_BASE_URL` | no | default `/api` |
| `APP_TITLE` | no | default `Assistant` (also used on `/data-deletion.html`) |
| `APP_TAGLINE` | no | default `Sign in to continue.` |
| `APP_RETENTION_DAYS` | no | integer 1..3650 (`^[1-9][0-9]{0,3}$`, entrypoint fails otherwise); same value as backend `Chat:RetentionDays` (one Bicep param `chatRetentionDays` feeds both); written to `config.json` `app.retentionDays` (number, only when set; the SPA shows a retention note) and used by `/data-deletion.html` |
| `APP_SUPPORT_CONTACT` | no (prod: yes, by convention) | plain text, max 200 chars, e.g. an email address; written to `config.json` `app.supportContact` and `/data-deletion.html`; rendered as text, never as HTML |

Backend (`appsettings.json` structure; values via env vars `AzureAd__Instance` etc. per environment):
```json
{
  "AzureAd": {
    "Instance": "https://<sub>.ciamlogin.com/",
    "TenantId": "<tid>",
    "ClientId": "<api-id>"
  },
  "Auth": { "RequiredScopes": "access_as_user" }
}
```
`AzureAd:Audience` is intentionally unset (Microsoft.Identity.Web accepts `<api-id>` and `api://<api-id>`).
The backend is unaffected by D6: email OTP users get normal access tokens with `oid`, `scp`, `aud` like social users.

| Value | Frontend key | Backend key |
|---|---|---|
| Authority / instance | `auth.authority` = `https://<sub>.ciamlogin.com/` | `AzureAd:Instance` (same) + `AzureAd:TenantId` = `<tid>` |
| SPA client id | `auth.clientId` | - |
| API client id / audience | inside `auth.apiScopes` | `AzureAd:ClientId` |
| API scope | `auth.apiScopes[0]` = `api://<api-id>/access_as_user` | `Auth:RequiredScopes` = `access_as_user` |

## 8. Checks after setup
- Decode an access token from the SPA (dev): `aud` = `<api-id>`, `scp` contains `access_as_user`, `oid` present,
  `ver` = `2.0`, record the exact `iss` for the backend integration test (ADR 0001, Q1). If `iss` or the login redirects
  use `<tid>.ciamlogin.com`, add that host to `AUTH_KNOWN_AUTHORITIES`.
- Call `GET /api/workflows` with and without the token: 200 / 401 (401 body is ProblemDetails `code: unauthorized`).
- nonprod: "Sign in with email" opens the hosted page with the email option; an OTP sign-up yields a token with `oid`.
- prod: the hosted page (generic sign-in / user flow "Run user flow") shows only Google and Facebook (5.2).
- `https://<frontend-fqdn>/data-deletion.html` loads without sign-in and shows no placeholder tokens (4.1).
- "Delete my data" in dev: afterwards `GET /api/sessions` returns an empty list and a second call returns 204.

## 9. Account deletion (manual admin step, D7)

"Delete my data" removes chat data only. The Entra External ID user object (the sign-in account) is separate and is
deleted manually by the operator (the user who administers the tenant; a tenant admin with the User Administrator
role or higher). There is no automation in v1.

Procedure (prod tenant; same in nonprod):
1. The person asks via the support contact (`APP_SUPPORT_CONTACT`, shown on `/data-deletion.html`).
2. Ask them to sign in first and use "Delete my data" (it can only run as that user). If they cannot or will not,
   their chat data is removed by retention instead: within `Chat:RetentionDays` of the last activity (prod default
   90 days). There is no admin purge tool in v1 (known limitation, plan.md section 6, D7).
3. Entra admin center -> switch to the tenant -> Entra ID -> Users -> find the user (search by display name or email;
   the Identities column shows Google/Facebook/email) -> select -> Delete.
4. The user moves to Deleted users and can be restored for 30 days. For immediate removal: Users -> Deleted users ->
   select -> Delete permanently.
5. Effects: access tokens already issued stay valid until they expire (about an hour); after that every API call
   fails with 401 and sign-in no longer finds the old account. If the person signs in again later, a NEW user object
   with a new `oid` is created and starts with an empty history.
6. Record the request date and completion date in your own records (no personal data in the repo).
