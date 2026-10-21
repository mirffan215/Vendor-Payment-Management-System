# ADR-0009: Vendor portal authentication and data isolation
Status: Accepted
Date: 2026-10-21

## Context
Vendors are external, unmanaged identities who can submit bank details. A compromised vendor account must not expose
internal data or other vendors' data (risk R13). Staff sign in through the customer's identity provider (ADR-0006);
vendors usually have none.

## Decision
- **Credentials:** invite link (256-bit token stored as a hash, single use, expiring, issued by staff), local password
  (PBKDF2-HMAC-SHA512, 600,000 iterations, rehash on login, 12+ characters, deny-list), and **mandatory TOTP**
  (RFC 6238, replay protection) with 10 hashed single-use recovery codes. No session exists without MFA.
- **Anti-enumeration:** every failed step returns the same 401 `invalid_credentials`; unknown users are verified against
  a dummy hash; lockout after 5 failures for 15 minutes; strict rate limit on `/portal/auth/*`.
- **Recovery:** staff re-issue invites; removing an authenticator needs two different staff (request, approve).
- **Separate session:** its own cookie (`__Host-vpms-vendor`, SameSite=Strict, 8 h absolute), claim `vpms:ctx=vendor`.
  Internal policies require `vpms:ctx=internal`, vendor policies require `vendor`; neither satisfies the other (tested
  both ways). The vendor id comes only from the server-issued session, never from a route, query, header or body.
- **Data isolation:** vendor-owned entities implement `IVendorOwned`. While a vendor session is active, a named EF query
  filter limits every vendor-owned table (and the vendor master row) to that vendor on top of the tenant filter, and
  `SaveChanges` refuses rows of another vendor. Another vendor's id is indistinguishable from an unknown id (404).
- The portal surface is a fixed set of operations (`IVendorPortalService`) with reduced DTOs: no reviewer identities,
  comments, ERP accounts or callback references.

## Alternatives considered
- Federated vendor login only (Entra B2B / Google): kept as a future option behind the same session shape; many
  vendors have no suitable IdP.
- SMS or email OTP instead of TOTP: weaker and adds a delivery dependency.
- SQL Server row-level security for the vendor dimension: not done. RLS predicates read `SESSION_CONTEXT`, which today
  carries tenant only; the EF filter, write guard and tests carry this layer. Adding a vendor key to the session context
  is a possible Phase 8 hardening.

## Consequences
- We own password and MFA storage; the surface is kept to standard primitives and reviewed against ASVS L2 in Phase 8.
- Invite delivery is behind `IInviteDelivery` (logged in development; email arrives with Phase 7).
- Data-protection keys must be shared and protected in production (Phase 8).
