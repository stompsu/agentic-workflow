# Auth setup: Entra External ID with Google and Facebook

Manual portal steps, run by the user. Verified against Microsoft Learn (External ID pages updated 2026-09).
Nothing here is automated by Bicep or pipelines. Placeholders: `<sub>` = tenant subdomain (from `<sub>.onmicrosoft.com`),
`<tid>` = Directory (tenant) ID GUID, `<spa-id>`, `<api-id>` = application (client) IDs.

## 0. Tenant topology (recommendation)

Two external tenants:
- `nonprod` tenant: shared by dev and test, with separate app registrations per environment.
- `prod` tenant: production only.

Why not one tenant for all: prod customer identities, branding, consent and social-provider apps should not be
shared with environments where developers have admin rights and test accounts. Why not three: dev and test share the
same risk profile; a third tenant doubles Google/Facebook app setup and Facebook review effort for no isolation benefit.
Effect: `oid` values differ between nonprod and prod (expected; data never moves between them).
If the user prefers strict per-stage parity, repeat everything below for a separate `test` tenant (decision D4).

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
| dev | nonprod | `supportchat-api-dev` | `supportchat-spa-dev` |
| test | nonprod | `supportchat-api-test` | `supportchat-spa-test` |
| prod | prod | `supportchat-api-prod` | `supportchat-spa-prod` |

### 2.1 API app
1. Register (no redirect URI). Record `<api-id>`.
2. Expose an API -> Application ID URI -> Add -> accept `api://<api-id>` -> Save.
3. Add a scope: name `access_as_user`, Who can consent: Admins only, display names/descriptions
   "Access Support Chat as the signed-in user", State Enabled.
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
2. App settings -> Basic: copy App ID and App Secret. Set Privacy Policy URL, Terms of Service URL, User Data Deletion URL
   (real pages you host; required for Live mode), Category. Add platform -> Website -> Site URL (frontend URL). Save.
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
5. Nonprod: keep the app in development mode; only app roles/testers can sign in (add testers).
   Prod: Go live (data handling questions, business verification; plan lead time).

Entra admin center: External Identities -> All identity providers -> Built-in -> Facebook -> Configure:
Name `Facebook`, Client ID = App ID, Client secret = App Secret -> Save.

## 5. User flow (per tenant) and app assignment

1. Entra ID -> External Identities -> User flows -> New user flow. Name `SignUpSignIn`.
2. Identity providers: Google, Facebook (available only after sections 3-4). Email accounts: email one-time passcode is
   useful in nonprod for test users without social accounts; whether to offer it in prod is decision D6.
3. User attributes: Display Name (keep minimal; email comes from the provider when shared).
4. Create.
5. Open the user flow -> Applications -> Add application -> select the SPA app(s) of that tenant
   (`supportchat-spa-dev` and `supportchat-spa-test` in nonprod; `supportchat-spa-prod` in prod). An application can have
   only one user flow; one flow may serve several apps. The API app is not added.
6. Test: User flows -> SignUpSignIn -> Run user flow (or sign in from the SPA) with Google and Facebook.

Provider buttons in the SPA pass `domain_hint=google` / `domain_hint=facebook` (Learn "issuer acceleration").
Verify in the nonprod tenant that the hint skips the chooser; if not, the hosted page still shows both providers (Q6).

Identity note: the same person signing in once with Google and once with Facebook gets two separate user objects by
default, i.e. two different `oid`s and two separate chat histories (ADR 0001). No account linking in v1.

## 6. SPA redirect URIs (platform: Single-page application)

MSAL uses `redirectUri: "/"`, resolved to `<origin>/`; register exactly with the trailing slash.

| Env | Redirect URIs |
|---|---|
| dev | `http://localhost:5173/`, `https://<dev-frontend-fqdn>/` |
| test | `https://<test-frontend-fqdn>/` |
| prod | `https://<prod-frontend-fqdn>/` (or the custom domain once chosen) |

`<env-frontend-fqdn>` = Container App FQDN from azure-infra outputs, e.g. `ca-supportchat-web-dev.<env-unique>.<region>.azurecontainerapps.io`.
Set post-logout redirect to the same origin. Localhost is allowed only in the dev registration.

## 7. Resulting configuration

Frontend runtime config (`/config.json`, generated at container start from env vars; local: `public/config.json`, git-ignored):
```json
{
  "auth": {
    "clientId": "<spa-id>",
    "authority": "https://<sub>.ciamlogin.com/",
    "redirectUri": "/",
    "apiScopes": ["api://<api-id>/access_as_user"],
    "providers": [
      { "id": "google",   "label": "Continue with Google",   "domainHint": "google" },
      { "id": "facebook", "label": "Continue with Facebook", "domainHint": "facebook" }
    ]
  },
  "apiBaseUrl": "/api"
}
```
Container env vars: `AUTH_CLIENT_ID`, `AUTH_AUTHORITY`, `AUTH_API_SCOPE`, `AUTH_PROVIDERS` (JSON, optional; default both).
None of these are secrets.

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

| Value | Frontend key | Backend key |
|---|---|---|
| Authority / instance | `auth.authority` = `https://<sub>.ciamlogin.com/` | `AzureAd:Instance` (same) + `AzureAd:TenantId` = `<tid>` |
| SPA client id | `auth.clientId` | - |
| API client id / audience | inside `auth.apiScopes` | `AzureAd:ClientId` |
| API scope | `auth.apiScopes[0]` = `api://<api-id>/access_as_user` | `Auth:RequiredScopes` = `access_as_user` |

## 8. Checks after setup
- Decode an access token from the SPA (dev): `aud` = `<api-id>`, `scp` contains `access_as_user`, `oid` present,
  `ver` = `2.0`, record the exact `iss` for the backend integration test (ADR 0001, Q1).
- Call `GET /api/workflows` with and without the token: 200 / 401 (401 body is ProblemDetails `code: unauthorized`).
