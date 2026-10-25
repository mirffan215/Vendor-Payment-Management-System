# Security findings register (Phase 8)

Method: reading the code for each ASVS chapter, writing tests for what the reading suspected, fixing, and keeping the test. Severity: Critical, High, Medium, Low
(likelihood x impact for the described deployment). "Closed" means fixed and covered by a test or a CI check named in the last column.

**Acceptance rule: no High or Critical finding is open.** At the time of writing: 0 Critical, 0 High open; 3 Medium and 4 Low open or accepted, below, each needing your decision or a follow-up.

## Closed
| ID | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| F-1 | High | Data-protection keys not persisted or protected: cookies, antiforgery and MFA challenges broke with 2+ instances; keys in plaintext on disk | Key ring in blob storage, wrapped by a Key Vault key; Production start-up guard | `ProductionGuardTests`, `SecurityBaselineTests` |
| F-2 | High | `Secrets:*` assumed to come from Key Vault but nothing loaded them; no check at start-up | Key Vault configuration provider, one secret source, start-up guards | `ProductionGuardTests` |
| F-3 | High | Forwarded headers not handled: rate-limit buckets collapsed to the proxy address; with empty lists ASP.NET would have trusted every sender | Headers honoured only from configured proxies/networks, otherwise ignored | `SecurityBaselineTests` (found by a failing test) |
| F-4 | Medium | SPA served by stock nginx: no CSP/HSTS/frame/nosniff, root user, no SPA fallback | Unprivileged image, headers, fallback, `nginx -t` in CI | `security.yml`, `SecurityBaselineTests` |
| F-5 | High | Most controllers checked permissions inside actions; a forgotten check meant "any signed-in staff user"; no coverage test | Endpoint metadata test with explicit allow-list | `EndpointAuthorizationTests`, `EndpointSweepTests` |
| F-6 | High | Vendor child routes (`Verify`, `RejectDocument`, `Reply`, `CloseRequest`) used the child id without checking the parent vendor | Ownership checks | `VendorApiTests.A_document_or_request_is_only_reachable_through_its_own_vendor` |
| F-7 | High | SSRF: D365 base URL, OIDC authority, webhook and SMTP hosts accepted private/link-local addresses; DNS not resolved; redirects followed | `OutboundGuard` (resolve, validate, connect to the validated address, no redirects) | `OutboundGuardTests` |
| F-8 | Medium | No request, header, form or JSON limits; uploads buffered unbounded; dev virus scanner allowed in Production | Kestrel and form limits, upload limiter, Production guard | `SecurityBaselineTests` |
| F-9 | Medium | No step-up for bank-detail reveal, payment release or role changes | `[RequireStepUp]`, `auth_time` check | `StepUpAndSessionTests` |
| F-10 | Medium | No server-side session revocation (only user disable) | `SessionsValidFrom` and revoke endpoints | `StepUpAndSessionTests` |
| F-11 | Medium | No log redaction; authz denials and CSRF failures not logged; worker without structured logs/OTel | Redacting enricher/formatter, `SecurityEventMiddleware`, shared observability block | `LogRedactionTests` |
| F-12 | Medium | No SAST, image scan, SBOM or Dependabot; actions and images unpinned | CodeQL, Trivy, CycloneDX, Dependabot, SHA/digest pinning, locked restore | `security.yml`, `ci.yml` |
| F-13 | Low | Unbounded paging parameters; `/auth/denied` reflected free-text reasons | Range attributes; reason allow-list | `EndpointSweepTests` |
| F-14 | Medium | Audit anchors in a temp-folder file sink: an attacker with database and app-host access could rewrite history undetectably | Blob sink in a separate account with an immutability policy; verifier compares the database with the anchors; restore markers | `AuditChainJobTests`, `RestoreDrillTests` |
| F-15 | Medium | No retention, disposal or offboarding: data could not be removed on request or at contract end | ADR-0024: policy, legal hold, disposal of operational data, crypto-shredding | `RetentionTests`, `TenantOffboardingTests` |

## Open or accepted (need a decision)
| ID | Severity | Finding | Recommendation | Owner decision |
|---|---|---|---|---|
| FR-3 | Medium | VPMS does not verify that MFA happened (`amr`/`acr` claims). Staff sign in through the customer's identity provider; its conditional-access policy decides. | Add a per-provider "require MFA" setting that checks `amr`/`acr`, before the first customer with a regulated profile | Accept for the first release, or build (about one slice) |
| FR-6 | Medium | Per-tenant data-key rotation has no operator command and no re-encryption job; key material of a leaked tenant key cannot be retired short of crypto-shredding | Add `rotate-data-key` to the migrator and a background re-encryption job | Accept with the runbook limit, or build |
| FR-7 | Medium | Nothing has been deployed: the Bicep, the blue/green pipeline, private endpoints, alerts and the Entra-only SQL path are compiled and linted, not run | First deployment to dev, then test, with the checklist in [deployment.md](../operations/deployment.md) | Needs an Azure subscription |
| FR-1 | Low | No third-party penetration test | Commission one against the test environment before go-live | Needs budget |
| FR-2 | Low | CodeQL requires GitHub Advanced Security on private repositories; without it the job reports but cannot upload | Enable it, or remove the job and rely on review | Needs a licence decision |
| FR-4 | Low | Per-tenant secret names (ERP, notification credentials) are validated when "Test connection" is run, not at start-up | Acceptable: a missing secret fails one tenant's integration visibly, not the service | None |
| FR-5 | Low | The web app's container address stays reachable when Front Door is not used (dev), and the Front Door id is not a secret | Production uses private ingress or origin lock-down as a later hardening step | None |

No finding was found that allows cross-tenant access, bypasses segregation of duties, or moves money. That is a statement about what the review looked for, not a proof.
