---
name: external-social-auth
description: Social login (Google, Facebook, ...) via Microsoft Entra External ID: SPA with MSAL, API with JWT validation. Use for any auth work.
---
Flow: SPA (MSAL, auth code + PKCE) -> Entra External ID (federated with Google/Facebook) -> access token for the API scope -> API validates JWT.
Backend: Microsoft.Identity.Web JWT bearer, authority = the `ciamlogin.com` tenant authority, audience = API app id. Authorization policy
requires the `oid` claim and the API scope. Frontend: MSAL config from runtime config.json, silent token acquisition, redirect on 401.
Dev: local redirect URIs registered in the dev tenant; one app registration per environment.
Never trust a userId from the request body or route: always take it from the token.