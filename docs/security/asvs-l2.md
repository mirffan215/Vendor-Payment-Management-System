# OWASP ASVS 4.0.3 Level 2: self-assessment

Status: Phase 8, 2026-10-25. This is the project's own review against the ASVS chapters, with the evidence in the repository. It is **not** a third-party
assessment and not a penetration test; recommendation 1 in the [findings register](findings-register.md) is to commission one before go-live.
Status values: **Met** (built and tested), **Partly** (built with a stated gap), **Not met** (not built; in the register), **N/A** (does not apply, with the reason), **Deploy** (depends on the deployed
environment, which has not been run; checked by reading the Bicep and the guards only).

| Chapter | Requirement (summary) | Status | Evidence |
|---|---|---|---|
| V1 Architecture | Documented architecture, trust boundaries, threat model | Met | `docs/architecture.md`, [threat-model.md](threat-model.md), ADRs 0001-0025 |
| V1 | Central, reusable security controls (authz, tenancy, audit) | Met | `BuildingBlocks` (tenancy, `IPermissionEvaluator`, `AuditPort`), architecture tests on module boundaries |
| V2 Authentication | No local passwords for staff; delegated to the tenant's identity provider (Entra, Google, OIDC) | Met | ADR-0006; `AuthenticationTests` against a fake OIDC provider |
| V2 | Vendor portal: strong factor, lockout, throttling | Met | ADR-0009; `PortalAuthTests` (MFA challenge, lockout, expiry) |
| V2 | Re-authentication for sensitive actions | Met | `[RequireStepUp]`, `auth_time` ≤ 2 minutes, ADR-0023; `StepUpAndSessionTests` |
| V2 | Multi-factor assurance verified from the token (`amr`/`acr`) | **Not met** | FR-3 in the register: MFA is the identity provider's policy; VPMS does not check that it happened |
| V3 Session management | Cookie `Secure`, `HttpOnly`, `SameSite`, no token in URL, server-side revocation | Met | `SecurityBaselineTests` (cookie attributes), `CookieSessionValidator`, `SessionsValidFrom`, `/session/revoke-all` |
| V3 | Idle and absolute timeouts; logout invalidates | Met | `Auth:IdleTimeout`/`AbsoluteTimeout`, vendor timeouts configurable; `StepUpAndSessionTests` |
| V3 | CSRF protection for cookie-authenticated state changes | Met | Antiforgery on all unsafe methods; CSRF failure logged as a security event |
| V4 Access control | Deny by default; every endpoint has an authorization decision | Met | `EndpointAuthorizationTests` (build fails for an action with no policy, permission check or allow-list entry), `EndpointSweepTests` |
| V4 | Tenant isolation at every layer | Met | EF global filters + SQL row-level security + tenant-scoped storage/cache/jobs; cross-tenant tests in every module's suite |
| V4 | Object-level authorization (IDOR) | Met | Vendor child routes verify the parent (`VendorsController`); cross-entity tests; `EndpointSweepTests` |
| V4 | Segregation of duties, maker-checker, two-person bank change | Met | ADRs 0008, 0015, 0017; SoD tests in Workflow and Payments |
| V5 Validation | Server-side validation, bounded inputs | Met | Length/range attributes on DTOs, paging `[Range]`, request/form/JSON limits (`HostLimits`), `SecurityBaselineTests` |
| V5 | Output encoding, no template injection | Met | React escaping; notification templates are data-driven with HTML encoding; template XSS tests; CSV/XLSX formula neutralisation tests (`ReportingTests`) |
| V5 | SSRF protection for server-side requests to user-supplied addresses | Met | `OutboundGuard` (resolve then connect, private/link-local/mapped addresses refused, no redirects) for webhooks, SMTP, Graph, D365, OIDC; `OutboundGuardTests` |
| V5 | File upload handling | Met | Size and type limits, quarantine then ClamAV scan, tenant-scoped paths, upload concurrency limiter (ADR-0010) |
| V6 Cryptography | Approved algorithms, key management, no hard-coded keys | Met | ADR-0004: AES-GCM envelope encryption, per-tenant DEKs wrapped by a Key Vault key; `ProductionGuards` refuse the Local key in Production |
| V6 | Key rotation and destruction | Partly | KEK rotation via Key Vault versions works; per-tenant data key rotation exists in code but has no operator command or re-encryption job (FR-6); crypto-shredding is built and tested (ADR-0024) |
| V7 Errors and logging | No sensitive data in logs; security events logged; tamper-resistant audit | Met | `LogRedaction` (account numbers, emails, tokens, connection strings), `SecurityEventMiddleware`, hash-chained audit with external anchors, `LogRedactionTests` |
| V7 | Time synchronisation and log protection | Deploy | Logs go to Log Analytics (365 days); access by role |
| V8 Data protection | Sensitive data encrypted at rest and in transit; minimal exposure | Met | Column encryption, TLS 1.2+ (SQL `Encrypt=True`, HSTS 1 year), reveal of bank details is permissioned, step-up, audited and masked by default (ADR-0018) |
| V8 | Retention and deletion | Met | ADR-0024, `RetentionTests`, `TenantOffboardingTests`; financial-record disposal deliberately not automatic (Q16) |
| V9 Communications | TLS everywhere; modern ciphers | Deploy | Container Apps ingress and Front Door enforce HTTPS; SQL `minimalTlsVersion 1.2`; storage `minimumTlsVersion` set in Bicep |
| V10 Malicious code | Dependencies reviewed; no back doors; integrity of build | Met | Locked NuGet restore, Dependabot, Trivy, CodeQL (needs GitHub Advanced Security on private repositories), SBOM, SHA-pinned actions and base-image digests |
| V11 Business logic | Sequential, rate-limited, tamper-proof flows | Met | Workflow engine state machine, re-approval on material change (ADR-0016), duplicate detection, rate limits (`RateLimiting`) |
| V12 Files and resources | Uploads not executable, not served inline from the app origin | Met | Download with `Content-Disposition: attachment` and `nosniff`; storage private; no public containers |
| V13 API | Versioned REST, content type checks, problem details, no mass assignment | Met | Typed DTOs, RFC 7807, OpenAPI; no entity binding |
| V14 Configuration | Hardened build and deployment; no default credentials; secure headers | Met | `ProductionGuards` (fail closed), CSP and security headers in nginx and API, non-root containers, `nginx -t` and non-root checks in CI |
| V14 | Dependency and component inventory | Met | CycloneDX SBOM per build (`security.yml`) |
| V14 | Separation of environments, secrets in a vault | Deploy | Bicep: Key Vault with RBAC and purge protection, managed identities; nothing in the repository (secret scanning in CI) |

## What this review changed (Phase 8 slices 1-4)
Data-protection key ring now persisted and protected by Key Vault; Key Vault configuration provider; production start-up guards; trusted-proxy handling; request limits;
hardened SPA container; endpoint authorization coverage test; SSRF guard; vendor child-route ownership; step-up re-authentication; server-side session revocation; log redaction and
security events; OpenTelemetry for API and worker; supply-chain CI. Details and severities: [findings-register.md](findings-register.md).

## Not covered
Penetration testing, dynamic scanning against a deployed environment, review of customers' identity-provider configuration, physical and organisational controls (see
[compliance-mapping.md](compliance-mapping.md) for what the customer or operator must provide).
