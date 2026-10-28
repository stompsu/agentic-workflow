# ADR 0006: Guest access ("Continue as guest")

Status: Accepted (2026-10-27, user), revision 2, branch `feature/guest-access`. Decisions G1..G18 resolved with the
recommended option (section 13). The CLAUDE.md amendment in section 3.2 is approved; the user makes that edit and it
must land before this branch merges. Implementation: plan.md section 7.3. Number 0005 is reserved for the local-mode
ADR on another unmerged branch.
Related: ADR 0001 (identity flow), ADR 0003 (Azure auth mode), ADR 0004 (retention and deletion), api-contract,
data-model, auth-setup. Decisions: section 13 (G1..G18, resolved).

Revision 2 (2026-10-27) folds in the qa-reviewer findings: one non-refreshable guest token per guest (the 30 min +
refresh design is now a rejected alternative, section 3.1), a concrete scheme discriminator, key handling across
replicas, global guest-turn caps, check ordering, a Cosmos conditional-patch quota, contract amendments to existing
sections, consistent `guest_expired`, client-IP parsing, `IGuestStore`, startup validation and frontend auth fixes.

## 1. Context

Today every user signs in with Google or Facebook (plus email OTP in nonprod) through Entra External ID. The user wants
a third option on the login page: use the app anonymously. A guest keeps chat sessions and history "as long as they
stay in the same browser session".

This conflicts with three hard rules in CLAUDE.md:
- "Login: social providers (Google, Facebook, ...) through Microsoft Entra External ID."
- "Backend validates JWTs only."
- "User identity = `oid` claim from the validated token. Every data access is scoped to it."

### 1.1 Does Entra External ID have an anonymous or guest sign-in? (checked 2026-10-27)

No. Microsoft Learn "Identity providers for external tenants" (ms.date 2026-03-27, updated 2026-06-17) lists the only
primary sign-in methods for external tenants: email + password, username/alias (preview, local accounts), email
one-time passcode, Google, Facebook, Apple, Microsoft Entra ID (OIDC federation), custom OIDC, and SAML/WS-Fed. Every one
of them creates a user object in the directory "with the identity information collected during sign-up". No method is
anonymous, account-less or device-based. Native authentication supports local accounts only (email OTP, email + password).
"Guest" in Entra means a B2B collaboration user, someone invited with their own identity. That is not anonymous
either. A client-credentials (app-only) token would not help: it is one identity for the whole app, and the `ChatUser`
policy already rejects app-only tokens (no `scp`).

So nothing in Entra fits. Email OTP is the closest thing, but it needs an email address and creates an account, which
is the opposite of what the user asked for. If guests are wanted, the backend has to mint the guest identity itself.

## 2. Naming and UX

Name (G1): the button says **"Continue as guest"**, matching "Continue with Google" / "Continue with Facebook". The
in-app badge says **"Guest"**. Rejected names: "Anonymous" (implies stronger privacy than we give: we still store the
chats, and IP-based rate limiting is applied), "Skip sign-in" (sounds like a shortcut to the same account).

Login page:
- The third button sits below the provider buttons, separated by an "or" divider. It is rendered only when
  `auth.guest.enabled` is true in `/config.json`.
- Caption under the button (plain text, built from config): "No account needed. Your chats stay only in this browser
  for up to {guestSessionHours} hours and are deleted automatically within {guestRetentionHours} hours after your last
  message." (`app.guestSessionHours` = `Guest:MaxLifetime` in hours, default 4; `app.guestRetentionHours` =
  `Guest:DataTtl` rounded up to whole hours, default 5.)

In the app (guest mode):
- The top bar shows a "Guest" badge instead of the user name, with a tooltip/short text: "Guest chats are not saved
  to an account. Close the browser and they are gone."
- The account menu for guests has "Sign in" (opens the login page) and "End guest session". "Delete my data" is not shown
  separately because ending the session deletes everything.
- "End guest session" opens a confirmation dialog: "This permanently deletes all your guest chats. This cannot be
  undone." On confirm the SPA aborts any stream, calls `DELETE /api/me/data` (which also ends the guest identity,
  section 6.5), clears the guest token from storage, tells other tabs (section 4), and shows the login page with the
  notice "Your guest session has ended and its chats were deleted." If the DELETE fails, the SPA still clears the token
  locally and says: "Your guest session has ended. Remaining chats will be deleted automatically within N hours."
- "Sign in" while a guest (G10): dialog "Signing in starts a separate account. Your guest chats will not be moved to it
  and will be deleted." Confirm: same as "End guest session", then `loginRedirect` with the chosen provider.
- The guest session ends at the token's `expiresAt` (default 4 h after "Continue as guest"; there is no renewal). The SPA
  sets a timer for `expiresAt`; when it fires, or when any API call returns 401 `guest_expired` (identity ended or data
  deleted in another tab), the SPA clears the token and shows the login page with "Your guest session has ended."
- Per-guest limits: when the turn quota is used up (`guest_quota_exceeded` on POST messages) the composer is disabled
  with "You have reached the guest limit. Sign in to keep chatting." When the session quota is used up
  (`guest_quota_exceeded` on `POST /api/sessions`) "New chat" is disabled with the same text. History stays readable.
- Sidebar retention note for guests: "Guest chats are deleted automatically within N hours after your last message."
  (N = `app.guestRetentionHours`), instead of the social-user note.

What a guest loses, stated in the UI: everything, once the browser session ends (section 4), the guest session reaches
its maximum lifetime, or the guest session is ended. The data cannot be recovered and cannot be moved to an account.

## 3. Identity mechanism: options

### (a) API-issued guest JWT, one per guest, signed by a Key Vault key the app cannot read (RECOMMENDED)

- `POST /api/guest/token` (anonymous, rate-limited) creates a random guest user id, writes the guest document
  (section 6.1) and returns ONE JWT whose `exp` is the end of the guest identity (`Guest:MaxLifetime`, default 4 h).
  There is no refresh endpoint and no second token for the same guest.
- Signature: ES256 with an EC P-256 key in the environment's existing Key Vault, created by Bicep. Key Vault never
  returns the private part of a key (Learn "Key types, algorithms, and operations": Get "doesn't return the private
  portion of an asymmetric key"; "The service doesn't support EXPORT operations"), no release policy is set, and the
  app identity holds only "Key Vault Crypto User". The backend only calls `sign` through its managed identity
  (`CryptographyClient.SignDataAsync`, package `Azure.Security.KeyVault.Keys`, latest stable 4.10.2). The app holds no
  secret.
- Validation is local and stateless at the token level: a second JWT bearer scheme `Guest` validates the signature
  against cached public keys (section 5.4), plus `iss`, `aud`, lifetime and `alg = ES256` only.
- Revocation: not by the token. The guest document is the revocation anchor. Writes check it authoritatively inside
  the Cosmos transaction (section 8), reads check it through a short per-replica cache (60 s). Deleting the guest
  document (`DELETE /api/me/data`) ends the guest everywhere within that bound.
- `ICurrentUser.UserId` = the token's `oid` claim, exactly as for Entra users. Stores, partitioning and isolation logic
  stay unchanged.
- Pros: the request path stays "validate a JWT, read `oid`". Standard Microsoft.IdentityModel validation (no custom
  crypto). No secret anywhere. One Key Vault `sign` per guest. No refresh flow, so no refresh endpoint, limiter or
  sleeping-tab failure mode.
- Cons: a new Azure data-plane dependency at issuance (not per request). A stolen guest token works until its `exp`
  (at most 4 h) unless the guest ends the session; the blast radius is one anonymous guest's chats and its remaining
  quota. Key Vault sign throughput is limited to 4,000 transactions per 10 s per vault for software EC P-256 / RSA 2048
  keys (Learn "Key Vault service limits"), far above guest issuance rates.
- Learn: Bicep/ARM cannot create new versions of an existing key ("Create a key vault and a key by using Bicep"), so
  redeploying is idempotent. Rotation is done by the key's rotation policy.

### (b) Opaque random guest token, stored hashed server-side

- Token = `gst_<guestId>.<256-bit random secret>`. Cosmos stores SHA-256(secret) in a credential document in the guest's
  partition. A custom `AuthenticationHandler` does a point read (1 RU) per request (could be cached briefly per replica)
  and compares hashes in constant time.
- Pros: no Key Vault dependency or key management. Instant revocation (delete the document). Same code path in every
  environment.
- Cons: it is not a JWT at all, so the "Backend validates JWTs only" rule needs a wider amendment. It needs a custom
  authentication handler, which is security-sensitive hand-written code. Every request gets a Cosmos read, adding RU
  cost and latency (or a cache that weakens revocation). And `ICurrentUser` becomes "oid from a token, or id from an
  opaque credential".

### (c) Entra-native

Does not exist (section 1.1). Reported, not improvised.

### (d) No guest access

Always an option. Users who do not want an account get nothing.

### Recommendation

Option (a). It keeps the identity model of ADR 0001 (a validated JWT whose `oid` scopes every data access), adds no
secrets, and keeps token validation stateless. Revocation is handled by the guest document (authoritative for writes,
60 s cached for reads).

### 3.1 Rejected alternative: 30 min access token + refresh endpoint (revision 1)

Revision 1 proposed `Guest:TokenLifetime` = 30 min, `POST /api/guest/token/refresh`, a `gst_exp` claim carrying the
absolute end of the identity, and 12 h maximum lifetime. Rejected because:
- It adds an endpoint, a claim, a refresh limiter, a refresh timer in every tab and a failure mode (a tab asleep for
  longer than 30 min with no other tab open loses the guest).
- Its benefit, a shorter window for a stolen token, is small: the operations that cost money or write data are already
  revoked by the guest-document check, reads by the cached check, and the identity itself now lives only 4 h.
- It needs a Key Vault `sign` per refresh per tab (about 16 per guest-hour with two tabs) instead of one per guest.
The refresh-limiter finding from the review (item 8) is moot with this simplification.

### 3.2 CLAUDE.md amendment (G3; approved by the user 2026-10-27; the user edits CLAUDE.md, agents never do)

Approved replacement text (2026-10-27). The user makes the edit in CLAUDE.md; it must land before this branch merges
(qa-reviewer checks it):
- Architecture, "Login:" line:
  "Login: social providers (Google, Facebook, ...) through Microsoft Entra External ID. Optional guest access
  (ADR 0006): the backend issues one guest JWT per guest (lifetime max 4 h), signed with a Key Vault key through its
  managed identity; no passwords, no accounts, no other login flow. Backend validates JWTs only (Entra tokens and its
  own guest tokens)."
- Hard rules, "User identity" line:
  "User identity = `oid` claim from the validated token (an Entra object id GUID, or `guest-<guid>` from a guest
  token; the validating scheme decides which form is allowed). Every data access is scoped to it."
- Unchanged: the Cosmos/Search `Azure:AuthMode` rule (ADR 0003) in every environment including Development/Test.
  Guest signing does not use `Azure:AuthMode`. Key Vault keys have no access-key path; deployed environments always use
  the managed identity, and local `Development` uses an in-process key (section 5.5). "AIProjectClient always uses a
  TokenCredential" is unchanged too.

ADR 0001 has a revision note pointing here (added 2026-10-27).

## 4. "Same browser session" semantics (G4)

| Option | New tab | Reload | Tab closed | Browser closed | Notes |
|---|---|---|---|---|---|
| A. `sessionStorage` per tab | new guest (empty) | same guest | lost | lost | Same storage MSAL uses (ADR 0001). Simplest. Surprising: a second tab is a different guest. |
| B. `sessionStorage` + `BroadcastChannel` hand-off (RECOMMENDED) | same guest if another tab of the app is open | same guest | kept while another tab is open | lost | Closest to "browser session" without cookies. |
| C. Session cookie (JS-readable, `Secure`, `SameSite=Strict`, no `Expires`) | same guest | same guest | kept | lost, except browsers with "continue where you left off" restore session cookies | Sent with every request to the origin (static files too). An HttpOnly cookie would need a cookie auth path + CSRF protection, which contradicts the contract's Authorization-header model. |
| `localStorage` | same guest | same guest | kept | KEPT | Rejected: outlives the browser session the user asked for. |

Recommendation B:
- The guest token and its `expiresAt` live in `sessionStorage` under one key (`agentwf.guest`), so they survive
  reloads in the same tab.
- Start-up order in `AuthContext`:
  1. MSAL has an account: social mode. Any guest token in this tab's `sessionStorage` is stale; remove it (no API call,
     no broadcast).
  2. Else this tab has a guest token with more than 60 s left: guest mode.
  3. Else: render the login page immediately (no blocking wait, so signed-in users and new visitors never wait) and
     post `{type:"guest-token-request"}` on `BroadcastChannel("agentwf-guest")`. If a `guest-token` reply arrives
     within 300 ms, store it and switch to guest mode. Later replies are ignored.
- A tab in guest mode answers a request only with its token if it has more than 60 s left (moot for most of the 4 h, but
  it avoids handing over a token that dies in the next request).
- "End guest session", "Sign in" from guest mode, logout in either mode, the `expiresAt` timer and `guest_expired`
  broadcast `{type:"guest-ended"}`. Every tab clears `agentwf.guest` and, if it was in guest mode, shows the login
  page.
- `BroadcastChannel` is same-origin only. It exposes nothing beyond what `sessionStorage` already exposes to scripts
  on our origin. XSS remains the main token-theft risk for every option. The existing CSP and the rendering rules
  (api-contract 8) are the mitigations.
- Browser close: all tabs are gone, so the token is gone. Some browsers restore `sessionStorage` on session restore. If
  the restored token is still before its `exp` it keeps working (at most 4 h after creation); otherwise the next call
  returns 401 `guest_expired`. Accepted and documented.
- Duplicating a tab copies `sessionStorage` in most browsers. That gives the same guest, which is expected.
- No renewal: the token is the guest. Its end is shown in advance only through the caption (section 2).

## 5. Token format, signing and validation

### 5.1 Guest JWT

Header: `{"alg":"ES256","typ":"JWT","kid":"<Key Vault key version>"}` (`kid` is the 32-character version id only, not
the vault URL). Claims:

| Claim | Value |
|---|---|
| `iss` | `Guest:Issuer`, e.g. `agentwf-guest-dev` (a non-URL string; Entra issuers are always https URLs) |
| `aud` | `Guest:Audience`, e.g. `agentwf-api-guest` (never an Entra audience, and the reverse) |
| `sub`, `oid` | the guest user id `guest-<random GUID v4, lowercase>` (122 bits from a CSPRNG) |
| `iat`, `nbf` | issue time |
| `exp` | issue time + `Guest:MaxLifetime` (default 4 h) = end of the guest identity |
| `jti` | random id (diagnostics only; not stored) |

No `scp`, no `gst_exp`, no name, no email, no IP, no device data.

### 5.2 Signing (JWS compact serialization, RFC 7515)

- Header and payload are serialized with System.Text.Json (no whitespace), UTF-8 encoded and base64url-encoded without
  padding.
- JWS signing input = ASCII(BASE64URL(UTF8(header)) || "." || BASE64URL(UTF8(payload))).
- Signature: `CryptographyClient.SignDataAsync(SignatureAlgorithm.ES256, signingInputBytes, ct)`. The SDK computes the
  SHA-256 digest locally (Learn API reference, Remarks: the hash algorithm is derived from the signature algorithm,
  SHA256 for ES256) and sends only the digest to Key Vault, which signs digests only (Learn: "the service doesn't
  hash content as part of signature creation"). The `CryptographyClient` is created for the selected VERSIONED key id
  (section 5.4), never the versionless one, so `kid` always matches the signing version.
- Token = signing input || "." || BASE64URL(signature). The signature must be the 64-byte R||S form required by JWS
  ES256 (V1).
- Why not `JsonWebTokenHandler.CreateToken` with a custom `SignatureProvider`: `SignatureProvider.Sign` is synchronous,
  which would force sync-over-async calls to Key Vault. Assembling the compact form is encoding only, not crypto.
  Validation still uses the standard handler.

### 5.3 Backend wiring (Microsoft.Identity.Web 4.16.1 stays for Entra)

Routing:
- The default scheme becomes a policy scheme `ApiBearer` whose `ForwardDefaultSelector` peeks at the bearer token's
  `iss` WITHOUT validating it (`JsonWebToken` read). If it equals `Guest:Issuer` and guests are enabled, the request
  goes to `Guest`; otherwise to the existing Microsoft.Identity.Web `Bearer` scheme. The peek only routes. It is never
  used for authorization.
- The `Guest` scheme (`AddJwtBearer`) is registered only when `Guest:Enabled = true`. `TokenValidationParameters`:
  `ValidIssuer = Guest:Issuer`, `ValidAudience = Guest:Audience`, `ValidAlgorithms = ["ES256"]`,
  `RequireSignedTokens = true`, `RequireExpirationTime = true`, `ClockSkew = 1 min`,
  `IssuerSigningKeyResolver` = `IGuestTokenSigner.ResolveValidationKey` (KeyVault mode: backed by
  `GuestSigningKeyCache`, section 5.4), and **`AuthenticationType = "guest"`** (constant `GuestAuth.AuthenticationType`).
  `MapInboundClaims = false`. The same ProblemDetails `OnChallenge`/`OnForbidden` handlers as the Entra scheme apply,
  plus one mapping: if `context.AuthenticateFailure` is a `SecurityTokenExpiredException`, the 401 body code is
  `guest_expired` instead of `unauthorized` (an expired guest token means the guest identity has ended, because `exp`
  is the identity end).

Discriminator (review item 1): the principal's identity kind comes ONLY from what the validating scheme stamped:
- Guest principal: `ClaimsIdentity.AuthenticationType == "guest"` (set only by the Guest scheme's validation
  parameters, never by token content) AND the validated `iss` claim equals `Guest:Issuer`.
- Entra principal: `AuthenticationType != "guest"` AND `iss` != `Guest:Issuer`.

`CurrentUser` (Api/Auth) changes:
```csharp
public enum UserKind { Entra, Guest }
// Single source of truth used by CurrentUser, ChatUserAuthorizationHandler, ChatRateLimiting and AuthExtensions.
public static bool TryGetUser(ClaimsPrincipal principal, out string userId, out UserKind kind);
// Kept as a wrapper for existing callers: TryGetUser(principal, out userId, out _).
public static bool TryGetUserId(ClaimsPrincipal principal, out string userId);
```
Rules of `TryGetUser`: exactly one authenticated identity. If its `AuthenticationType` is `"guest"`: `oid` must match
`^guest-[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`, kind = Guest. Otherwise: `oid` must be a
non-empty GUID (today's rule), kind = Entra. A GUID `oid` on a guest identity and a `guest-` `oid` on an Entra identity
both fail.

Authorization (both the named `ChatUser` policy AND the fallback policy, `AuthExtensions.AddChatAuth`):
- Both become `RequireAuthenticatedUser()` + one `ChatUserRequirement`, replacing today's `RequireScope(scopes)` +
  `RequireAssertion(TryGetUserId)` in both places (`RequireScope` would reject every guest, which has no `scp`).
- `ChatUserAuthorizationHandler` (DI: `IOptions<GuestOptions>`, the configured scopes):
  - `TryGetUser` fails: fail.
  - kind Guest: succeed only if `Guest:Enabled` and the identity's `iss` claim equals `Guest:Issuer`. No scope check.
  - kind Entra: succeed only if `iss` != `Guest:Issuer` and the required scope is present (the same claim types
    Microsoft.Identity.Web's `RequireScope` checks: `scp` and `http://schemas.microsoft.com/identity/claims/scope`).
- `ChatRateLimiting` keys by `TryGetUser` and picks `Guest:TurnsPerMinute` for kind Guest, `Chat:TurnsPerMinute` for
  Entra. Partition keys stay disjoint because the id formats are disjoint.
- `ICurrentUser` (Core) gains `bool IsGuest { get; }` (additive). `UserId` stays the partition key.
- Guests disabled: the `Guest` scheme is not registered and the selector never routes to it, so a guest token, even a
  correctly signed one, gets 401 `unauthorized` from the Entra scheme.

Required auth tests (part of the backend test list in plan.md section 7.3):
- Guest principal (`AuthenticationType "guest"`, guest `iss`) with a GUID `oid` -> 403 `forbidden`; `TryGetUser` false.
- Entra principal (default `AuthenticationType`, scope present) with a `guest-<guid>` `oid` -> 403; `TryGetUser` false.
- Principal with `AuthenticationType "guest"` but `iss` != `Guest:Issuer` -> 403 (handler unit test).
- End to end: a guest-key-signed token with an Entra-looking `iss` -> routed to Entra scheme -> 401; a token with
  `iss = Guest:Issuer` signed by any other key -> 401; `alg: none` / HS256 -> 401; wrong `aud` -> 401; expired guest
  token -> 401 `guest_expired`; expired Entra token -> 401 `unauthorized` (unchanged).
- Rate limiter: guest and Entra principals land in separate partitions with their own limits.
- V5: `AuthenticationType` from `TokenValidationParameters` reaches the `ClaimsIdentity` under the JwtBearer handler
  of the installed ASP.NET Core version (proved by the tests above with the real handler, not hand-built principals only).

### 5.4 Signing keys across replicas (review item 2)

Components (Api, internal):
- `GuestSigningKeyCache` (singleton): an immutable snapshot `{ verificationKeys by kid, signingVersion, loadedAt }`
  swapped atomically. Its `Resolve(kid)` backs `IGuestTokenSigner.ResolveValidationKey`, which is used as the
  `IssuerSigningKeyResolver`. That resolver is synchronous, so it NEVER calls Key Vault: it returns the cached key for
  `kid`; if `kid` is unknown (or the cache is still empty) it calls `RequestRefresh()` and returns no key, so that
  request fails with 401 `unauthorized`.
- `RequestRefresh()` is non-blocking and coalesced: it signals the refresher, at most one on-demand refetch per 30 s per
  replica (so a flood of tokens with random `kid` values costs at most two Key Vault list calls per minute per replica).
- `GuestSigningKeyRefresher` (`BackgroundService`, registered only when guests are enabled with `SigningMode = KeyVault`):
  - Prefill: `StartAsync` loads the snapshot before the app accepts traffic, with up to 3 attempts within 20 s. If that
    fails it logs an Error and lets the host start anyway (guest validation and issuance fail until a load succeeds;
    signed-in users are unaffected). Readiness is NOT tied to Key Vault, so a Key Vault outage cannot take the whole
    API down.
  - Periodic refresh every `Guest:SigningKeyRefreshInterval` (default 1 h) plus on-demand refreshes. On failure it
    keeps the last good snapshot, logs a Warning, and retries with backoff (5 s doubling to 5 min).
  - Load = `KeyClient.GetPropertiesOfKeyVersionsAsync(name)`, then `GetKeyAsync(name, version)` for every version that
    is enabled, has `NotBefore` <= now + 5 min (or none), and has `ExpiresOn` > now - `Guest:MaxLifetime` - ClockSkew
    (or none). Newest 10 versions at most. Each becomes an `ECDsaSecurityKey` with `KeyId = version`. Key Vault allows
    `verify` on expired keys, but we verify locally anyway, so tokens signed shortly before a version expired keep
    validating until their own `exp`.
- Signing version selection (recomputed at every load): the newest version that is enabled, allows `sign`, has
  `NotBefore` <= now, has `ExpiresOn` > now + `Guest:MaxLifetime` + 10 min (or none), and was created at least one
  refresh interval + 5 min ago. "Older than one refresh interval" guarantees every replica's periodic refresh has
  already loaded it into its verification set, so a token issued on replica A validates on replica B. If only younger
  eligible versions exist (first deployment, or all older ones became ineligible), the newest eligible one is used anyway;
  replicas that do not know it yet fetch it through the unknown-`kid` path, and the client's single delayed retry
  (section 7.3, client) covers that one 401.
- Key nearing or past expiry without rotation (Key Vault rejects `sign` outside the key's nbf/exp window; Learn "Key
  types, algorithms, and operations"):
  - The initial version created by Bicep has no expiry: Learn ("Configure key auto-rotation") says the rotation
    policy's `expiryTime` "sets the expiration date on the newly rotated key. It doesn't affect the current key."
    Versions created by rotation (every 90 days) get a 1-year expiry. Normally a version is replaced 9 months before it
    expires.
  - If the signing version expires within 30 days: Warning log event `GuestSigningKeyNearExpiry` at every refresh, and
    `--smoke-check` prints a warning.
  - If no version is eligible for signing (all expire within MaxLifetime + 10 min, are expired, or are disabled):
    `POST /api/guest/token` returns 500 `internal_error`, Error log event `GuestSigningKeyUnavailable`, and
    `--smoke-check` FAILS (blocks promotion). Existing guest tokens keep working until their `exp`. Token lifetimes are
    never shortened to fit under a key expiry (keeps "`exp` = identity end" true).
- Ownership and notification: the user (operator; infra is deployed from the user's machine, D11) owns the key. Recipient
  of notifications: the user, by email. v1 mechanism: azure-infra adds an Azure Monitor scheduled-query alert on the
  backend's `GuestSigningKeyNearExpiry` / `GuestSigningKeyUnavailable` log events with an action group whose email
  address is a deploy-time parameter (not committed). The rotation policy also has a Notify action 30 days before expiry
  that emits the Key Vault Event Grid near-expiry event; subscribing to it needs an Event Grid handler and is optional.
  Remedy: the user runs `az keyvault key rotate` (needs Key Vault Crypto Officer); replicas load the new version within
  one refresh interval and sign with it after that.

### 5.5 Signer per environment and interfaces

| Where | Signer | Notes |
|---|---|---|
| Local `Development` | `Guest:SigningMode = Ephemeral`: an ECDsa P-256 key generated in memory at process start, never persisted | No Azure dependency, no secret. Guest tokens die with the process; the SPA then ends the guest session after one retry (section 7.3, client). Startup fails if `Ephemeral` is used outside `Development`. |
| Dev / Test / Production (ACA) | `Guest:SigningMode = KeyVault`, `Guest:SigningKeyUri = https://<vault>.vault.azure.net/keys/guest-token-signing` (versionless) | `KeyClient`/`CryptographyClient` with `IAzureCredentialProvider.Credential` (the user-assigned managed identity, ADR 0003 section 3). Section 5.4. |
| Tests | the test host registers a fake `IGuestTokenSigner` (in-memory P-256 key) explicitly via `ConfigureTestServices` | Never relies on `Ephemeral`, which is Development-only (G14). No network. |

Interfaces (Api project, internal to auth):
```csharp
public interface IGuestTokenSigner   // KeyVaultGuestTokenSigner | EphemeralGuestTokenSigner | test fake
{
    string Algorithm { get; }                                                       // "ES256"
    Task<(string Kid, byte[] Signature)> SignAsync(byte[] jwsSigningInput, CancellationToken ct); // 64-byte R||S
    SecurityKey? ResolveValidationKey(string kid);   // synchronous, cache only; unknown kid -> null + refresh request
}
public interface IGuestTokenService  // builds header/payload, base64url, calls the signer
{
    Task<GuestToken> IssueAsync(string guestUserId, DateTimeOffset expiresAt, CancellationToken ct);
}
public sealed record GuestToken(string AccessToken, DateTimeOffset ExpiresAt);
```
V1: Key Vault returns ES256 signatures in the JWS form (raw 64-byte R||S, not DER). Proven by a round-trip test (Key
Vault sign, then Microsoft.IdentityModel validation) against the dev vault before relying on it.

## 6. Data (Cosmos)

### 6.1 Partitioning and the guest document

- Partition key: the guest user id `guest-<guid>`, in the same container `chat` with the same `/userId` partition key.
  Session and message documents are identical to social users'. Isolation: every store call is already scoped by
  `userId`. No guest can address another guest's or a social user's partition, or the other way round.
- The prefix marks guest data for the retention policy only (section 6.3). It is never used for an authorization
  decision; the token scheme decides that (section 5.3).
- Guest document (new type, one per guest partition, created at issuance BEFORE the token is signed, so a token never
  exists without its document; if signing then fails, the orphan document expires by TTL):
  ```json
  { "id": "guest", "type": "guest", "userId": "guest-3f0c...", "createdAt": "...", "expiresAt": "...",
    "turnCount": 0, "maxTurns": 30, "sessionCount": 0, "maxSessions": 5, "schemaVersion": 1, "ttl": 15300 }
  ```
  `maxTurns` and `maxSessions` are snapshots of `Guest:MaxTurns` / `Guest:MaxSessions` at creation, so a config change
  never changes the rules for a live guest. It is never returned by the API. Its `ttl` is `Guest:DataTtl` in seconds;
  Cosmos counts TTL from the last write (`_ts`), so every quota patch also extends it.
- Session quota (review item 13): `sessionCount` counts sessions CREATED (not decremented by deletes), max
  `maxSessions` (default 5) per guest identity.

### 6.2 `IGuestStore` (Core, new; Cosmos + InMemory implementations, review item 10)

```csharp
namespace AgenticWorkflow.Core.Storage;
public sealed record GuestRecord(string UserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    int TurnCount, int MaxTurns, int SessionCount, int MaxSessions);
public enum GuestCounter { CreationsPerHour, TurnsPerHour, TurnsPerDay }
public interface IGuestStore
{
    Task CreateAsync(GuestRecord guest, CancellationToken ct);                   // create guest doc (ttl = DataTtl)
    Task<GuestRecord?> GetAsync(string guestUserId, CancellationToken ct);       // uncached point read; null = ended
    Task<long> IncrementCounterAsync(GuestCounter counter, DateTimeOffset now, CancellationToken ct);
        // patch Increment on the window document; creates it on 404 (409 on the create -> patch again); returns new count
}
```
- Ensure-exists for reads (cached) is NOT in the store: `GuestActivityCache` (Api, singleton, `IMemoryCache`) wraps
  `GetAsync`: positive entries for `Guest:ActiveCheckCacheDuration` (default 60 s), negative entries until the token's
  `exp`. It also caches "quota exhausted" for a guest once seen, until `exp` (section 8). `DELETE /api/me/data`
  invalidates this replica's entry immediately.
- Increment turns / sessions are NOT separate `IGuestStore` calls: they run inside the existing session-store
  transactions so a failed turn start or session create never consumes quota (section 8). Changes to `ISessionStore`
  (Core/Storage/Storage.cs):
  - `CreateAsync(string userId, string workflowId, string? title, bool enforceGuestQuota, CancellationToken ct)` returns
    `CreateSessionResult` = `Created(SessionRecord)` | `GuestExpired` | `GuestQuotaExceeded`.
  - `TryStartTurnAsync(string userId, string sessionId, MessageRecord userMessage, bool enforceGuestQuota, CancellationToken ct)`;
    `StartTurnResult` gains `GuestExpired` and `GuestQuotaExceeded`.
  - `enforceGuestQuota` is passed by the endpoint from `ICurrentUser.IsGuest` (identity decides, not the id prefix).
  - Cosmos: the existing batch (or a new batch for session create) adds
    `batch.PatchItem("guest", [PatchOperation.Increment("/turnCount", 1)], new TransactionalBatchPatchItemRequestOptions { FilterPredicate = "FROM c WHERE c.turnCount < c.maxTurns" })`
    (sessions: `/sessionCount`, `c.sessionCount < c.maxSessions`). Per-operation results: the guest operation 404 ->
    `GuestExpired`; 412 -> `GuestQuotaExceeded`; the other operations then report 424 and nothing is written. An
    existing session-ETag conflict keeps today's retry/`turn_in_progress` handling.
  - InMemory: the same checks under the store's existing lock, so both stores behave identically (shared tests).
- Verified: `TransactionalBatchPatchItemRequestOptions.FilterPredicate` exists (Learn .NET API reference, listed for
  Microsoft.Azure.Cosmos 3.58.0; installed pin is 3.63.2; patch needs 3.23.0+), and Learn "Get started with partial
  document update" shows conditional patch inside a transactional batch. To verify before relying on it (V3, blocking):
  the 412 status for a failed predicate, the property-to-property comparison `c.turnCount < c.maxTurns` (fallback: a
  literal from the snapshot read at creation), the 404/412/424 per-operation results alongside the existing replace +
  create operations, and the same behavior in the Cosmos emulator used by the tests.

### 6.3 Retention (review item 10; refactor of `TurnRules.TtlSeconds`)

- Today `TurnRules.TtlSeconds(int? retentionDays)` returns null when `Chat:RetentionDays` is unset (local Development),
  and is called with `Options.RetentionDays` at every write.
- Refactor: a `RetentionPolicy` (Infrastructure, injected into both stores) with `int? TtlSeconds(string userId)`:
  - `guest-` prefix: ALWAYS `(int)Guest:DataTtl.TotalSeconds` (default 04:15:00 = 15,300 s), even when
    `Chat:RetentionDays` is unset and even when `Guest:Enabled` is false (leftover guest data still expires).
  - otherwise: today's rule (`RetentionDays * 86400` or null).
- Call sites to switch (all pass `userId` instead of `Options.RetentionDays`): `CosmosSessionStore` lines 37, 98, 196,
  279, 282, 323, 329; `InMemoryChatStore` line 351 (`Ttl()`); `TurnRulesTests` moves to `RetentionPolicyTests`. The
  guest document and the counters use their own TTLs (sections 6.1, 6.4).
- R1-R6 of data-model 1.3 stay: messages get `ttl` once at create, session documents on every write. Container
  `defaultTtl = -1` is already set in every environment (infra/modules/cosmos.bicep), so per-document `ttl` applies.
- Why 4 h 15 min (G5): every document of a guest is written at or after the guest's creation, and Cosmos counts TTL
  from the last write, so `DataTtl >= MaxLifetime + 5 min` guarantees nothing expires while the token can still read it
  (5 min > the 1 min clock skew). After the guest leaves, all data is gone at most `DataTtl` after the guest's last
  write: typically within about 4 h of the last message, in the worst case about 8 h 15 min after "Continue as guest"
  (last message just before the 4 h end). Startup rule: `DataTtl >= MaxLifetime + 00:05:00`.

### 6.4 Global counters

- Reserved partition `userId = "system-guest-counter"`. Documents `create-h-yyyyMMddHH`, `turn-h-yyyyMMddHH`,
  `turn-d-yyyyMMdd` (UTC), `{ "count": n, "ttl": 172800 }`, incremented with patch `Increment` (atomic). No user id can
  equal this value: Entra ids are GUIDs and guest ids start with `guest-`.
- Load: one patch per guest creation and two per guest turn, all into one logical partition. At the default caps (a few
  hundred per hour) this is far below a logical partition's throughput limit.

### 6.5 Delete my data and the end of a guest

- `DELETE /api/me/data` for a guest runs the existing data-model 1.7 sequence on the guest partition, then deletes the
  guest document last, and invalidates this replica's `GuestActivityCache` entry. Result: all data gone and the guest
  identity ended. The token stays cryptographically valid until `exp`, but writes fail at once (404 on the guest
  operation in the batch -> 401 `guest_expired`) and every other request fails within 60 s (cached check).
- `DELETE /api/me/data` skips the guest-activity check, so calling it for an already-ended guest runs the (empty)
  sequence and returns 204 (idempotent, same as 2.8 for a user with no data).
- No merge with or access to a social account (G10). A later "upgrade" (moving a guest partition to an `oid` partition
  when the guest signs in) would need a call carrying both tokens and a cross-partition copy. That is out of scope and a
  separate decision.
- Indexing: no change (`type` is already indexed; the guest doc is read by id).

## 7. API contract (applied to api-contract.md on 2026-10-27)

The text below was applied to api-contract.md on 2026-10-27 (sections 1, 2, 2.8, 2.9, 3, 5, 6, 7). api-contract.md is
now the source of truth and wins on any difference. All of it is additive or a clarification (no version bump).
SSE (section 4) is unchanged.

### 7.1 Changes to section 1 (General rules)

Replace the "Auth:" bullet with:
> - Auth: every endpoint except health and `POST /api/guest/token` requires `Authorization: Bearer <access token>`. Two
>   token kinds are accepted: (1) an Entra External ID access token for scope `api://<api-client-id>/access_as_user`
>   (/docs/adr/0001-identity-flow.md); (2) when guest access is enabled in the environment, a guest token issued by
>   `POST /api/guest/token` (/docs/adr/0006-guest-access.md). Clients treat both as opaque strings.

Replace the "The user is ALWAYS the `oid` claim" bullet's first sentence with:
> - The user is ALWAYS the `oid` claim of the validated token (an Entra object id, or a guest id for guest tokens).

Add:
> - Guests: a guest is a user with no account. Every endpoint behaves the same for guests unless stated otherwise in
>   2.9. Responses never say whether the caller is a guest; the client knows which token it holds.

### 7.2 Amendments to existing text (review item 6)

- Section 2.8, "Request" bullet 2 (currently "Auth exactly like every other endpoint: bearer token with scope
  `access_as_user` and an `oid` claim (401 `unauthorized` / 403 `forbidden`, section 3)"), replace with:
  > - Auth exactly like every other endpoint (section 1): an Entra token with scope `access_as_user` and an `oid` claim,
  >   or a guest token (401 `unauthorized` / 403 `forbidden`, section 3). The data deleted is ALWAYS the token's `oid`
  >   partition; `me` is not an id and no other user can be addressed.
- Section 2.8, append to the responses table's 204 row: "For a guest, also ends the guest identity (2.9). Calling it
  again with the same guest token after the guest has ended still returns 204."
- Section 3, 403 bullet, replace with:
  > - 403: valid token that is not a chat user: an Entra token missing the `access_as_user` scope or a valid GUID `oid`
  >   (for example an app-only token), or a guest token whose `oid` is not a guest id. Body ProblemDetails
  >   `code: forbidden`. 403 is never used for resources.
- Section 3, 401 bullet, append: "For guest tokens, 401 `guest_expired` means the guest identity has ended (maximum
  lifetime reached or data deleted). The client must NOT retry; it goes back to the login page. A guest token has no
  refresh."
- Section 5 table, `forbidden` row "When": "Entra token lacks the scope or a valid `oid`; guest token with an invalid
  guest id."
- Section 5 table, `rate_limited` row "When": "Per-user limiter on POST messages (keyed by `oid`, per replica; guests
  have a lower limit), the guest-creation limits on `POST /api/guest/token` (per client address and per environment),
  and the global guest-turn caps on POST messages (per environment). Header `Retry-After` in seconds. Client: disable
  the action until `Retry-After` elapses."
- Section 5 table, `unsupported_media_type` row: append "Also `POST /api/guest/token` (for consistency with the other
  JSON endpoints)."

### 7.3 New endpoint row (section 2) and new section 2.9

| Method | Path | Auth | Success | Notes |
|---|---|---|---|---|
| POST | `/api/guest/token` | none | 200 | Create a guest identity and return its only token. Section 2.9. |

> ### 2.9 Guest access
>
> Available only when the environment enables it (`Guest:Enabled`). When disabled, `POST /api/guest/token` returns 404
> `guest_access_disabled` and guest tokens are rejected with 401 `unauthorized`.
>
> **POST /api/guest/token**
>
> Request headers: `Content-Type: application/json` (required; other types -> 415), no `Authorization`.
> Body: `{}`. When bot protection is enabled (decision G7): `{ "botCheckToken": "<provider token>" }`.
> Unknown properties are ignored.
>
> Response 200 (`Cache-Control: no-store`):
> ```json
> { "accessToken": "<jwt>", "tokenType": "Bearer", "expiresAt": "2026-10-27T14:15:00.000Z" }
> ```
> - `expiresAt`: when the token AND the guest identity end (default 4 h after issue). There is no refresh; after this
>   time the guest is gone.
> - The guest starts with no sessions. Every call creates a NEW guest; there is no way to return to an earlier guest.
>
> Errors: 404 `guest_access_disabled`; 415 `unsupported_media_type`; 429 `rate_limited` + `Retry-After` (per-client
> or global guest-creation limit); 400 `bot_check_failed` (only when bot protection is enabled); 500 `internal_error`
> (for example, the signing service is unavailable).
>
> **Guest behavior on other endpoints**
> - Any endpoint: 401 `guest_expired` when the guest token has expired or the guest identity was ended by
>   `DELETE /api/me/data` (writes: immediately; other requests: within about one minute). Exception:
>   `DELETE /api/me/data` returns 204 for an ended guest.
> - `GET /api/workflows` returns only the workflows enabled for guests (possibly fewer than for signed-in users).
>   `POST /api/sessions` with a workflow not enabled for guests returns 400 `validation_failed`, like an unknown id.
>   A guest session whose workflow is no longer enabled for guests returns 409 `workflow_unavailable` on POST messages.
> - `POST /api/sessions`: each guest may create at most `Guest:MaxSessions` sessions in its lifetime (deleting one does
>   not give it back). Beyond that: 429 `guest_quota_exceeded` WITHOUT `Retry-After`.
> - `POST /api/sessions/{id}/messages`: the per-user limiter is stricter for guests (`Guest:TurnsPerMinute`, 429
>   `rate_limited` + `Retry-After`). Global guest-turn caps per hour and per day apply to all guests together (429
>   `rate_limited` + `Retry-After` until the window ends). Each guest also has a total turn quota (`Guest:MaxTurns`);
>   when it is used up the response is 429 `guest_quota_exceeded` WITHOUT `Retry-After`, and nothing is persisted. A
>   turn counts against the quota once the server has accepted it (turn lock taken and user message stored, 2.6),
>   whatever happens afterwards: completed, failed, cancelled, or an error before the `session` event. Requests
>   rejected before that point (validation, limits, `turn_in_progress`, `not_found`) do not count.
> - `DELETE /api/me/data` for a guest deletes all the guest's chat data AND ends the guest identity. 204/503 as in 2.8.
> - Guest data expires `Guest:DataTtl` (default 4 h 15 min) after it was last written.
>
> Client (SPA): stores the guest token in `sessionStorage` (ADR 0006 section 4); ends guest mode at `expiresAt`; on
> 401 `guest_expired` does not retry, clears the token and shows the login page with a notice; on 401 `unauthorized`
> in guest mode retries once after 1 s with the same token, then ends guest mode; treats `guest_quota_exceeded` as
> "disable sending (or new chats), offer sign-in". It never sends the guest token anywhere except `/api`.

### 7.4 Section 5 (error table) new rows and section 6/7 additions

| status | code | When |
|---|---|---|
| 400 | `bot_check_failed` | `POST /api/guest/token` with a missing or rejected bot-check token (only when bot protection is enabled). |
| 401 | `guest_expired` | Guest token expired or guest identity ended. Client: no retry; go to the login page. |
| 404 | `guest_access_disabled` | `POST /api/guest/token` when guest access is disabled in this environment. |
| 429 | `guest_quota_exceeded` | Guest used up its session or turn quota. No `Retry-After`. Client: disable the action, offer sign-in. |

Section 6 limits, new rows: guest identity and token lifetime 4 h (`Guest:MaxLifetime`); guest data TTL 4 h 15 min
after last write (`Guest:DataTtl`); guest turns per minute 5 per replica (`Guest:TurnsPerMinute`); guest total turns
30 (`Guest:MaxTurns`); guest sessions 5 (`Guest:MaxSessions`); guest creations 10 per client address per hour per
replica (`Guest:CreationsPerClientPerHour`) and 200 per hour per environment (`Guest:MaxCreationsPerHour`); guest
turns 300 per hour and 2,000 per day per environment, all guests together (`Guest:MaxTurnsPerHour`,
`Guest:MaxTurnsPerDay`).

Section 7 (infrastructure), new bullet: "nginx sets `X-Client-Ip` in `location /api/` only (overwriting any client
value) to the rightmost `X-Forwarded-For` entry received from the Container Apps ingress; the backend uses only this
header, and only for the guest-creation limit (ADR 0006 section 9.4)."

`isGuest` in responses: not added. There is no `/api/me` endpoint and the SPA does not need one: it knows its token kind.

## 8. Server-side guard order

`POST /api/guest/token`:
1. 404 if guests are disabled; 415 if not JSON; 400 `bot_check_failed` when bot protection is on.
2. Per-client in-memory limiter (`Guest:CreationsPerClientPerHour`, key from section 9.4) -> 429 + `Retry-After` from
   the limiter lease. Skipped when no client address could be parsed.
3. Global creation cap, in-memory "closed until" flag -> 429, no Cosmos call.
4. `IncrementCounterAsync(CreationsPerHour)`; if the new count > cap: set the flag, 429.
5. `IGuestStore.CreateAsync` (guest document), then sign the token (Key Vault) -> 200.

`POST /api/sessions` (guest):
1. Authentication and `ChatUser` (section 5.3); `GuestActivityCache` -> 401 `guest_expired`.
2. Body validation, workflow allowed for guests -> 400.
3. Session create with the guest session-quota patch in one batch -> `GuestExpired` = 401 `guest_expired`,
   `GuestQuotaExceeded` = 429 `guest_quota_exceeded`.

`POST /api/sessions/{id}/messages` (guest), in this order:
1. Per-guest per-minute limiter (in memory, existing middleware) -> 429 `rate_limited`.
2. `GuestActivityCache`: guest ended -> 401 `guest_expired`; cached "quota exhausted" -> 429 `guest_quota_exceeded`
   (so an exhausted guest cannot keep spending global-counter writes).
3. Existing request validation; workflow still allowed for guests -> 409 `workflow_unavailable`.
4. Global guest-turn caps, in-memory "closed until" flags (hour, day) -> 429 `rate_limited`, no Cosmos call.
5. `IncrementCounterAsync(TurnsPerHour)` and `(TurnsPerDay)`; if either new count > its cap: set that flag, 429.
6. `TryStartTurnAsync(..., enforceGuestQuota: true)`: guest-document conditional patch + existing session replace +
   user message create in ONE transactional batch -> `GuestExpired` = 401 `guest_expired`, `GuestQuotaExceeded` = 429
   `guest_quota_exceeded` (and cache "exhausted"), existing conflict results unchanged. Nothing is persisted on any
   failure.
7. Turn runs. From here the quota unit is consumed whatever happens (section 7.3).

Accepted over-count: a request rejected at step 6 has already incremented the global counters at step 5, and several
replicas can each pass step 4 before any of them sees the cap. The global counters may therefore exceed their caps by
about (replicas x concurrent requests) per window. They count attempts, not completed turns; this errs on the side of
lower spend.

## 9. Abuse and cost controls

Guests can spend model tokens without an account. The controls are layered. None of them stops a determined attacker
with many IPs on its own, so the global caps are the cost ceiling.

### 9.1 Switches and per-guest limits
1. Per-environment switch `Guest:Enabled` (default false). Bicep `guestAccessEnabled`: dev true, test true, prod false
   until the user approves (G8). Preconditions for prod: G7 (bot protection) and the prod Key Vault role (section 12).
   The frontend gets the same value (`AUTH_GUEST_ENABLED`).
2. Per-guest: `Guest:TurnsPerMinute` 5 (per replica), `Guest:MaxTurns` 30 and `Guest:MaxSessions` 5 per identity
   (Cosmos-enforced), identity lifetime 4 h. `Chat:MaxMessageChars` is unchanged.
3. Workflow availability: `Guest:Workflows` (string array of workflow ids, default `["support-ticket"]`). The API
   filters the registry per caller; workflows stay identity-agnostic. Startup fails if guests are enabled and none of
   the listed ids is registered.

### 9.2 Global caps and ordering (review items 3 and 4)
- Creation cap `Guest:MaxCreationsPerHour` (default 200 per environment).
- Guest-turn caps `Guest:MaxTurnsPerHour` (default 300) AND `Guest:MaxTurnsPerDay` (default 2,000), across all guests,
  checked at turn start (section 8, steps 4-5). The hourly cap stops a burst from eating the model capacity signed-in
  users need right now; the daily cap bounds spend.
- Order: in-memory limiters first (per client, per guest), then the in-memory "closed" flags, then the Cosmos counters.
  When a counter exceeds its cap, the replica caches "closed until the window ends" and stops writing to that counter
  until then.
- `Retry-After` for global caps = seconds until the end of the closed window (next UTC hour, or next UTC midnight for
  the daily cap), rounded up, minimum 1; if both turn caps are closed, the later one. For the in-memory limiters it
  comes from the limiter lease metadata (existing `OnRejected` code).
- Over-count behavior: accepted, section 8.

### 9.3 Cost ceiling and protecting signed-in users (G15, G16)
- Without global turn caps, the worst case was 200 guests/h x 24 h x 30 turns = 144,000 guest turns per day. With
  them: guest turns per day <= min(`MaxTurnsPerDay`, 24 x `MaxTurnsPerHour`) + over-count = 2,000 (+ a few per replica)
  at the defaults.
- Worst-case cost of one turn: 1 orchestrator call + at most `MaxToolCallsPerTurn` + 1 specialist calls (4 at the
  default 3), each with input bounded by the system prompt, the history window, the search results and a 4,000-char
  message; plus up to 3 query embeddings; plus Cosmos RU and one Key Vault sign per new guest (negligible).
- True worst-case daily guest spend = min(`MaxTurnsPerDay`, 24 x `MaxTurnsPerHour`) x (cost of 1 orchestrator call +
  4 x cost of a maximal specialist call + 3 embeddings), priced with the current per-token prices of the deployments
  configured for the guest workflows. The user sets the caps from this (G15). Prices are not hard-coded here because
  they change.
- Protecting signed-in users (G16), options:
  - (a) Caps only (RECOMMENDED for v1): set `MaxTurnsPerHour` so that guest peak load stays below about 25% of the
    smallest TPM quota among the deployments used by guest workflows (MaxTurnsPerHour x maximal tokens per turn / 60 <=
    0.25 x TPM). Simple and no new resources. Residual risk: a burst inside one hour can still cause model 429s for
    everyone.
  - (b) Separate lower-quota model deployments for guests: register a second instance of the SupportTicket workflow
    (e.g. id `support-ticket-guest`) whose `Agents:*:Deployment` settings point to guest-only deployments, and list only
    it in `Guest:Workflows`. Model throttling then hits guests only. Costs: the registry must support a second,
    config-parameterized registration of one definition (agent-workflows task), extra deployments supplied by the user,
    and more Bicep params. Recommended before raising the caps or enabling prod with large traffic.

### 9.4 Client address for the creation limiter (review item 9)
- Source: Learn "Ingress in Azure Container Apps": the ingress appends to `X-Forwarded-For`, and "only the rightmost
  IP is provided by Azure Container Apps"; earlier entries are client-controlled. nginx (frontend app, behind the
  external ingress) extracts the rightmost entry and sets `X-Client-Ip`, overwriting any client value. The `map` lives
  in a `conf.d` file (http context, outside `server {}`); the header is set ONLY in `location /api/`, next to the
  existing `proxy_set_header` lines there:
  ```nginx
  # conf.d/client-ip-map.conf (http context)
  map $http_x_forwarded_for $client_ip {
      "~(?<rightmost>[^,\s]+)\s*$" $rightmost;
      default                       $remote_addr;
  }
  # default.conf.template, inside location /api/ only:
  proxy_set_header X-Client-Ip $client_ip;
  ```
- Backend parsing (only when `Guest:TrustClientIpHeader = true`, set by Bicep in ACA; locally the connection address):
  trim; strip a port from `a.b.c.d:port`; strip brackets and port from `[v6]:port`; a bare IPv6 address is taken whole;
  then `IPAddress.TryParse`; IPv4-mapped IPv6 becomes IPv4; IPv6 keys use the /64 prefix (privacy addresses rotate inside
  a /64). `X-Forwarded-For` is ignored entirely and ForwardedHeaders middleware is not enabled.
- Fail mode: missing or unparsable value -> no per-client bucket for that request (NOT one shared "unknown" bucket that
  one abuser could fill for everyone). The request is still subject to the global creation cap, and the guest it
  creates to all per-guest and global turn limits. A counter metric `guest.client_ip_unparsed` (no value logged)
  makes a broken header chain visible.
- The backend has internal ingress (reachable only from apps in the environment, all ours), so only nginx can set the
  header. CGNAT and offices share addresses, so the per-client limit stays moderate (10 per hour per replica).
- V2 (blocking for relying on the per-client limit): verify the real header chain once in dev (log the parsed form at
  Debug, temporarily, without storing it).

### 9.5 Bot protection (G7)
- Not in v1, but a documented precondition for enabling guests in prod: a CAPTCHA (Cloudflare Turnstile, hCaptcha,
  Google reCAPTCHA) on the "Continue as guest" button, verified server-side in `POST /api/guest/token`
  (`botCheckToken`). It adds an external dependency, a secret key (Key Vault), a privacy notice and a CSP change.
- Azure Front Door Premium WAF JavaScript challenge: Learn states it does not support AJAX/API calls, and our token
  request is a fetch call. It would also put a new Front Door in front of Container Apps. Not a fit as-is.
- `Content-Type: application/json` is required on `POST /api/guest/token` for consistency with the other JSON
  endpoints (415 otherwise). It is not claimed as a CSRF control: the endpoint is anonymous and has nothing to protect
  from cross-site use beyond the rate limits above.

## 10. Logging and privacy

- Never logged: tokens (guest or Entra), `botCheckToken`, raw IP addresses, `X-Client-Ip`, `X-Forwarded-For`. Rate-limit
  rejections log only the limiter name and the partition kind ("client", "global-create", "global-turn-hour",
  "global-turn-day", "guest").
- Logged: guest creation outcome (counts; `enduser.id` = the guest id, which is random and links to no person), quota
  and `guest_expired` rejections with `enduser.id`, key events `GuestSigningKeyNearExpiry` / `GuestSigningKeyUnavailable`
  (key name and version only).
- IP use: only in memory, as a rate-limiter partition key, for at most one window (1 h). Never persisted. The global
  counters store counts only.
- Application Insights: keep the default IP masking (do not set `DisableIpMasking`).
- Existing finding, not caused by this ADR (G12): the nginx image's default `main` access log format includes
  `"$http_x_forwarded_for"`, so client IPs are already written to the frontend container logs (Log Analytics). This
  gets more relevant once anonymous use is offered. Proposed: a custom `log_format` without client IPs (or with
  `$remote_addr` only, which is the ingress IP).
- Privacy text: the login caption (section 2) and, optionally, a sentence on `/data-deletion.html` ("Guest chats are
  deleted when you end the guest session, and automatically within N hours after your last message.").

## 11. Configuration (backend; env form `Guest__*`) and startup validation

| Key | Default | Notes |
|---|---|---|
| `Guest:Enabled` | `false` | Bicep `guestAccessEnabled` (dev/test true, prod false until approved) |
| `Guest:SigningMode` | `KeyVault` | `KeyVault` / `Ephemeral`; `Ephemeral` only in `Development` |
| `Guest:SigningKeyUri` | - | versionless key URI; required when Enabled and mode `KeyVault` |
| `Guest:SigningKeyRefreshInterval` | `01:00:00` | `00:05:00`..`01:00:00` (section 5.4) |
| `Guest:Issuer` | - | required when Enabled; `^[a-z0-9-]{3,64}$`, e.g. `agentwf-guest-<env>` |
| `Guest:Audience` | `agentwf-api-guest` | `^[a-z0-9-]{3,64}$`; must not be an Entra audience |
| `Guest:MaxLifetime` | `04:00:00` | `00:30:00`..`12:00:00`; token `exp` = identity end |
| `Guest:DataTtl` | `04:15:00` | `>= MaxLifetime + 00:05:00`, `<= 7.00:00:00`; range `00:35:00`..`7.00:00:00` checked even when guests are disabled |
| `Guest:ActiveCheckCacheDuration` | `00:01:00` | `00:00:10`..`00:05:00` |
| `Guest:TurnsPerMinute` | `5` | per guest per replica |
| `Guest:MaxTurns` | `30` | 1..1000 per guest identity (snapshot on the guest document) |
| `Guest:MaxSessions` | `5` | 1..100 per guest identity (snapshot on the guest document) |
| `Guest:CreationsPerClientPerHour` | `10` | per replica |
| `Guest:MaxCreationsPerHour` | `200` | per environment (Cosmos counter) |
| `Guest:MaxTurnsPerHour` | `300` | per environment, all guests (Cosmos counter) |
| `Guest:MaxTurnsPerDay` | `2000` | per environment, all guests; `<= 24 x MaxTurnsPerHour` |
| `Guest:TrustClientIpHeader` | `false` | Bicep sets `true` in ACA |
| `Guest:Workflows` | `["support-ticket"]` | env form `Guest__Workflows__0` |

Startup validation (ADR 0003 section 4, rules 12-17; rule 11 is left to ADR 0005 on the local-mode branch, see the
reconciliation note there). When `Guest:Enabled = true`:
- `Guest:Issuer` present and matching the pattern (so it can never equal an Entra issuer, which is always an https
  URL); additionally it must not equal any issuer the Entra scheme accepts (derived from `AzureAd:Instance` /
  `AzureAd:TenantId`, or configured explicitly).
- Audiences differ both ways: `Guest:Audience` is not `AzureAd:ClientId`, `api://<ClientId>` or any configured
  `AzureAd:Audience(s)`, and none of the Entra scheme's valid audiences equals `Guest:Audience`.
- `SigningMode = Ephemeral` only in `Development` (always fails in `Production`).
- `KeyVault` mode: `SigningKeyUri` present, `https`, host ends with `.vault.azure.net`, path exactly `/keys/<name>`
  (versionless: a version segment fails startup).
- The ranges above; `DataTtl >= MaxLifetime + 00:05:00`; `MaxTurnsPerDay <= 24 x MaxTurnsPerHour`; at least one
  `Guest:Workflows` id is registered.
When `Guest:Enabled = false`, the other keys are ignored.

`--smoke-check` (ADR 0003 1a), when guests are enabled and mode is `KeyVault`: loads the key versions, selects the
signing version (fails if none is eligible, warns if it expires within 30 days), signs a fixed test payload and
verifies it with the loaded public key. A missing "Key Vault Crypto User" assignment shows up here as a 403 from Key
Vault and fails the smoke check with a message naming the role, before prod traffic.

Frontend env (none secret): `AUTH_GUEST_ENABLED` (`true`/`false`, default false) -> `config.json` `auth.guest.enabled`;
optional `AUTH_GUEST_LABEL` (default "Continue as guest") -> `auth.guest.label`; `APP_GUEST_SESSION_HOURS` (1..12,
= `Guest:MaxLifetime` in hours) -> `app.guestSessionHours`; `APP_GUEST_RETENTION_HOURS` (1..168, = `Guest:DataTtl`
rounded up) -> `app.guestRetentionHours`.

## 12. Infra and pipelines

- Key Vault (existing vault per environment, RBAC mode): new key `guest-token-signing`, `kty: EC`, `curveName: P-256`,
  `keyOps: ['sign', 'verify']`, no release policy, no `exp` on the initial version. Rotation policy: Rotate 90 days after
  creation, Notify 30 days before expiry, `expiryTime` P1Y (applies to rotated versions; G13). Created only when
  `guestAccessEnabled` is true. Bicep never creates new versions, so redeploys are idempotent (Learn). Old versions stay
  enabled after rotation; the app stops signing with them but keeps verifying them while tokens they signed can live.
- RBAC: the app's user-assigned managed identity gets "Key Vault Crypto User" (`12338af0-0e69-4776-bea7-57ae8d297424`)
  on the vault, in every environment where guests are enabled. Today `infra/modules/keyvault.bicep` assigns only
  "Key Vault Secrets User", and only when `kvReaderPrincipalId` is non-empty, which is empty in prod. So the module needs
  a separate parameter (e.g. `cryptoUserPrincipalId`, assigned when guests are enabled, independent of the secrets
  role). Enabling guests in prod therefore requires this new prod role assignment (G8). The test MI smoke job uses the
  same identity. The deploying user (D11) already has the control-plane rights needed to create keys.
- Alerting (section 5.4): Azure Monitor scheduled-query alert on `GuestSigningKeyNearExpiry` / `GuestSigningKeyUnavailable`
  and an action group emailing the user (address = deploy-time param `alertEmail`, not committed). New for this repo.
- Backend env: `Guest__Enabled`, `Guest__SigningMode=KeyVault`, `Guest__SigningKeyUri`, `Guest__Issuer`,
  `Guest__MaxLifetime`, `Guest__DataTtl`, `Guest__TrustClientIpHeader=true`, plus optional cap overrides. The smoke job
  gets the same non-secret settings.
- Frontend env: `AUTH_GUEST_ENABLED`, `APP_GUEST_SESSION_HOURS`, `APP_GUEST_RETENTION_HOURS` (derived from the same
  Bicep params as the backend values, so they cannot drift).
- New Bicep params: `guestAccessEnabled` (bool; dev true, test true, prod false), `guestMaxLifetimeHours` (4),
  `guestDataTtlMinutes` (255), optional `guestMaxCreationsPerHour`, `guestMaxTurns`, `guestMaxSessions`,
  `guestMaxTurnsPerHour`, `guestMaxTurnsPerDay`, `alertEmail`.
- Pipelines: no change expected. Images do not change per environment, infra is deployed from the user's machine (D11),
  and CI already runs the backend and frontend tests. devops-pipelines owns `.github` and is not asked to change anything.

## 13. Decisions (resolved 2026-10-27 by the user: every recommendation accepted)

Each item keeps the original question with the recommendation in brackets; "Resolved" records the user's decision.

G1. Name of the option. ["Continue as guest"; badge "Guest"] Resolved: "Continue as guest", badge "Guest".
G2. Identity mechanism: (a) API-issued ES256 JWT signed by a Key Vault key via managed identity, (b) opaque hashed
    token, (d) no guest access. [(a)] Resolved: (a).
G3. Approve the CLAUDE.md amendment text in section 3.2 (you edit CLAUDE.md). [approve as written] Resolved: approved
    as written; the user makes the CLAUDE.md edit, which must land before merge.
G4. "Same browser session": A per-tab, B per-tab + hand-off between open tabs, C session cookie. [B] Resolved: B.
G5. Lifetimes: one token per guest, `exp` = guest max lifetime, no refresh (30 min + refresh rejected, section 3.1);
    data TTL after each write. [lifetime 4 h; data TTL 4 h 15 min (= lifetime + margin; worst case ~8 h 15 min after
    creation, typically ~4 h after the last message)] Resolved: lifetime 4 h, data TTL 4 h 15 min.
G6. Per-guest limits and quota rules: 30 turns, 5 sessions (created, not given back on delete), 5 turns/min, 10 new
    guests per client address per hour (per replica); an accepted turn consumes quota whatever its outcome, rejected
    requests do not. [as proposed] Resolved: as proposed.
G7. Bot protection (CAPTCHA, external provider). [not in v1 (dev/test); required before enabling prod] Resolved: not
    in v1; required before enabling prod.
G8. Per-environment switch. [dev on, test on, prod OFF; enabling prod requires G7 and a new "Key Vault Crypto User"
    assignment for the app identity in prod, which prod infra does not have today] Resolved: dev on, test on, prod
    off; enabling prod is a separate later decision with those preconditions.
G9. Workflows available to guests. [config allow-list `Guest:Workflows`, default `support-ticket`] Resolved: as
    recommended.
G10. Guest -> account: no merge or upgrade in v1; signing in from guest mode ends the guest session and deletes its
     data. [yes; upgrade path is a later decision] Resolved: yes.
G11. "End guest session" = delete all guest data + end the identity, one action with confirmation. [yes] Resolved: yes.
G12. Stop writing client IPs to the nginx access log (existing behavior, default log format). [yes, custom log format]
     Resolved: yes, custom log format without client IPs.
G13. Signing key: EC P-256 software key, rotate every 90 days, rotated versions expire after 1 year, initial version
     without expiry. [yes; HSM / Premium vault not needed] Resolved: yes.
G14. Local `Development`: ephemeral in-process signing key; tests register a fake signer explicitly. [yes] Resolved:
     yes.
G15. Global caps: 200 new guests per hour, 300 guest turns per hour and 2,000 per day per environment (Cosmos
     counters). [as proposed; set from the worst-case formula in section 9.3 and your cost tolerance] Resolved: as
     proposed (defaults; the user may change them per environment through the Bicep params).
G16. Protecting signed-in users' model capacity: (a) caps only, (b) separate lower-quota guest deployments via a guest
     workflow variant. [(a) for v1; (b) before raising caps or enabling prod with real traffic] Resolved: (a) for v1.
G17. `guest_expired` on reads after "End guest session" in another tab: cached guest-document check (60 s per replica,
     about 1 RU per guest per minute per replica) vs relying on the `guest-ended` broadcast only. [cached check]
     Resolved: cached check.
G18. Signing-key expiry notifications go to you by email via an Azure Monitor alert on the app's key log events
     (Event Grid near-expiry subscription optional). [yes; you own manual rotation if auto-rotation ever fails]
     Resolved: yes.

## 14. Verification items (Phase 1, before relying on them)

V1. Key Vault ES256 signature format is JWS-compatible (round trip: Key Vault sign -> Microsoft.IdentityModel validate)
    in dev.
V2. (Blocking for the per-client limit.) Real `X-Forwarded-For` chain at nginx in dev: the rightmost entry is the client
    address; ports/brackets as parsed in section 9.4.
V3. (Blocking for the quota implementation.) Cosmos transactional batch with `PatchItem` + `FilterPredicate` +
    `Increment` on the guest document alongside the existing replace + create: 412 on a failed predicate, 404 on a
    missing guest document, 424 on the other operations, nothing written; property-to-property predicate; same behavior
    in the emulator. Installed Microsoft.Azure.Cosmos 3.63.2.
V4. `BroadcastChannel` hand-off works in current Chrome, Edge, Firefox and Safari (Safari supports it since 15.4).
V5. `TokenValidationParameters.AuthenticationType = "guest"` is visible as `ClaimsIdentity.AuthenticationType` under the
    installed JwtBearer handler; an expired guest token reaches `OnChallenge` with `SecurityTokenExpiredException`.

## 15. Consequences

- A new, unauthenticated, cost-bearing entry point exists. Its daily ceiling is the global guest-turn caps (section 9.3).
- The backend issues tokens (sign only, via Key Vault). It still runs no login flow and stores no credentials.
- One new document type (`guest`) and one reserved partition (`system-guest-counter`) in the `chat` container; the
  retention computation moves from "by config" to "by user".
- Guests lose everything when the browser session ends or after 4 h. This is intended, and the UI says so.
- Entra social sign-in, ADR 0003 key/MI rules, the SSE protocol and the workflow abstraction are unchanged. The
  authorization policies change shape (one custom requirement instead of `RequireScope` + assertion) but keep today's
  rules for Entra tokens.
