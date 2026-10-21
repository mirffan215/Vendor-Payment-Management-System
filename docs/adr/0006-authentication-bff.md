# ADR-0006: Authentication — BFF cookie sessions with per-tenant OpenID Connect
Status: Accepted
Date: 2026-10-21

## Context
Customers sign in with their own identity provider: Microsoft Entra ID, Google, or any OpenID Connect provider.
The tenant must never be taken from anything a client or an external token can influence. The single-page app
should not hold access tokens (XSS exposure).

## Decision
- **Backend-for-frontend**: the API runs the OIDC authorization-code flow with PKCE and issues an HttpOnly,
  Secure session cookie (`__Host-vpms`). No provider tokens are stored in the browser or in the cookie
  (`SaveTokens = false`). The SPA only calls same-origin `/api` and `/auth`.
- **Providers are data**: `identity.IdentityProvider` rows (tenant, kind, authority, client id, secret *reference*,
  scopes, allowed email domains, auto-provision flag). One OIDC scheme per provider is registered at runtime
  (`oidc-<providerId>`, callback `/auth/callback/<providerId>`); a middleware registers the scheme before its callback
  so any instance can serve it. Entra authorities must be tenant-specific (`common`, `organizations`, `consumers`
  are rejected).
- **Tenant comes from the server**: the tenant is the tenant that owns the provider configuration. Claims inside
  the external token (including any `tid`) are ignored; only server-derived claims enter the session
  (`vpms:uid`, `vpms:tid`, `vpms:pid`, `vpms:ctx=internal`, `vpms:auth_time`).
- **Provisioning**: unknown users are created only when the provider allows auto-provisioning and the (verified)
  email domain is on the provider's allow list; disabled users and unverified emails are refused. Every sign-in
  and every denial is audited (no personal data in denials).
- **Session**: sliding idle timeout (default 30 min), absolute lifetime (default 8 h, enforced through
  `vpms:auth_time`), user status re-checked at most once a minute. Cookie `SameSite=Lax` because the return from the
  IdP is a cross-site navigation; state-changing requests therefore require an antiforgery header
  (`X-XSRF-TOKEN`, token from `GET /api/v1/session`).
- **Vendor portal** (Phase 2) gets its own scheme name, cookie and `vpms:ctx=vendor` claim, so a vendor session can
  never satisfy an internal policy. Internal policies require `vpms:ctx=internal`.
- **Sign-out** clears the VPMS session only; single sign-out at the IdP is out of scope for v1.
- Client secrets are resolved through `ISecretResolver` (`Secrets:<ref>` configuration, filled from Key Vault).

## Alternatives considered
- Bearer tokens in the SPA (MSAL/oidc-client): token theft risk in the browser, harder tenant binding.
- Static OIDC schemes in configuration: cannot support customer-managed providers.
- `SameSite=Strict`: the first request after the IdP redirect would arrive without the session cookie.

## Consequences
- Adding a provider needs no deployment. Changing a provider's settings requires evicting its scheme (an admin
  operation to be added with the Phase 2 admin UI).
- Each instance keeps the schemes it has seen in memory; startup cost is paid on first use.
- Refresh tokens are not used: the session cookie slides, and re-authentication happens at the absolute lifetime.
