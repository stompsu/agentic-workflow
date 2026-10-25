# ADR 0001: Identity flow (SPA token -> API -> `oid` -> Cosmos partition key)

Status: Accepted (Phase 0, revised 2026-10-25). Skill: external-social-auth.

## Context

End users sign in with Google or Facebook. The SPA is a public client; the backend must not run any login flow,
store tokens or trust client-supplied identity. All user data lives in Cosmos partitioned by user.

## Decision

```
Browser (SPA, MSAL auth code + PKCE)
  -> https://<tenant-subdomain>.ciamlogin.com/   (Entra External ID user flow: Google | Facebook)
  <- access token  aud = <API client id>, scp = access_as_user, oid = <user object id>
  -> GET/POST /api/...  Authorization: Bearer <token>   (same origin, nginx proxies /api)
API (Microsoft.Identity.Web JWT bearer)
  -> validate signature (tenant JWKS), issuer, audience, lifetime
  -> authorization policy: scope access_as_user + oid present
  -> ICurrentUser.UserId = oid
  -> every store call: (userId = oid, ...) -> Cosmos PartitionKey(oid)
```

### SPA
- `@azure/msal-browser` v5 (5.26.0) + `@azure/msal-react`, `PublicClientApplication`, `loginRedirect`,
  authorization code + PKCE (MSAL default for SPA platform redirect URIs).
- Config from runtime `/config.json`: `authority = https://<tenant-subdomain>.ciamlogin.com/`, `clientId` = SPA app id,
  `knownAuthorities` (hosts; default = authority host; may need `<tenant-guid>.ciamlogin.com`, see Q1),
  `apiScopes = ["api://<api-client-id>/access_as_user"]`, `redirectUri = "/redirect.html"` (resolved to
  `<origin>/redirect.html`). MSAL v5 requires `redirectUri` to point to a redirect bridge page that calls
  `broadcastResponseToMainFrame()` (MSAL.js `lib/msal-browser/docs/redirect-bridge.md`, `v4-migration.md`).
  Post-logout redirect URI = `<origin>/`. Registered URIs: auth-setup section 6.
- Login request includes the API scope so consent/token for the API happen at sign-in.
- Provider buttons come from config; each passes `extraQueryParameters: { domain_hint: "google" | "facebook" }`
  (documented "issuer acceleration" values). If the hint does not route reliably in our tenant, buttons fall back to
  the hosted page, which lists both providers (Q6).
- Every API call: `acquireTokenSilent({ scopes: apiScopes, account })`; on `InteractionRequiredAuthError` ->
  `acquireTokenRedirect`. On HTTP 401: one silent retry with `forceRefresh: true`, then redirect to login.
- Token cache: `sessionStorage` (Learn default). The SPA never sends user ids to the API.

### API
- Package `Microsoft.Identity.Web` 4.16.1.
- `AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"))`.
- Config: `AzureAd:Instance = https://<tenant-subdomain>.ciamlogin.com/`, `AzureAd:TenantId = <tenant GUID>`,
  `AzureAd:ClientId = <API app id>`. `AzureAd:Audience` left unset: Microsoft.Identity.Web then accepts
  `<ClientId>` and `api://<ClientId>` as audience. v2 access tokens carry the GUID.
- API app manifest: `requestedAccessTokenVersion = 2` (verify in each tenant; set explicitly).
- Fallback authorization policy `ChatUser` applied to all `/api` endpoints except health:
  `RequireAuthenticatedUser()`, `RequireScope("access_as_user")` (Microsoft.Identity.Web), and an assertion that
  the object id claim exists. Missing scope or `oid` -> 403 (app-only tokens have no `scp` and are rejected).
- 401/403 bodies: `JwtBearerEvents.OnChallenge` (call `HandleResponse()`, keep `WWW-Authenticate: Bearer`) and
  `OnForbidden` / the authorization result handler write ProblemDetails with `code` `unauthorized` / `forbidden`
  (api-contract 3). No token-validation detail in the body.
- `ICurrentUser` (scoped) reads `HttpContext.User.GetObjectId()` (Microsoft.Identity.Web extension, handles both
  `oid` and the mapped long claim name). It throws if absent; the endpoint layer never reaches stores without it.
- Issuer: Microsoft.Identity.Web reads the tenant metadata from the ciamlogin.com authority. Community reports show
  CIAM tokens with issuer `https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0`; Phase 1 includes an integration test
  with a real dev-tenant token to confirm validation passes (no custom issuer overrides unless that test fails; Q1).
  Part of Q1 on the SPA side: if the tenant issues tokens or redirects on the GUID host, add
  `<tenant-id>.ciamlogin.com` to the SPA `knownAuthorities` (`AUTH_KNOWN_AUTHORITIES`).

### Data scoping
- `userId` = `oid` string (lowercase GUID) is the Cosmos partition key and a filter on every query.
- Store interfaces take `userId` as their first parameter; the endpoint passes `ICurrentUser.UserId`. No store method
  can be called without it, and no DTO has a `userId` property.
- A session id from the route is always resolved as `(sessionId, PartitionKey(userId))`; another user's session is
  "not found" -> 404 identical to an unknown id except `traceId` (never 403; existence is not leaked).
- `oid` is stable per user object per tenant. It differs between tenants, so data in the non-prod tenant is never
  visible to the same person in prod (expected).
- Google and Facebook sign-ins of the same person create separate user objects by default, so they get different
  `oid`s and separate histories. No account linking in v1; the UI does not promise cross-provider history.

## Consequences
- Rejected: reading `sub` (pairwise per app, would change if the SPA registration changes), email (mutable, may be
  missing for Facebook), or any client-provided id.
- Logging: `oid` may appear in structured logs/traces as `enduser.id`; tokens, emails, names and message content are
  never logged (enforced from Phase 1).
- Account deletion (GDPR) is a delete-by-partition operation for that `oid` (future admin job; D7).
- Separate tenants per stage group mean separate `oid` spaces; test data cannot be promoted to prod (not needed).
- Isolation is proven by tests (data-model 1.5): user B cannot read, list, delete or lock user A's data.
