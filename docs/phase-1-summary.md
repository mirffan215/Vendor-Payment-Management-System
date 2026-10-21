# Phase 1 summary — Foundation

Status: complete, awaiting approval. Branch `claude/bold-wozniak-mkomqb`.

## What was built

| Area | Delivered |
|---|---|
| Scaffold | Clean Architecture modular monolith (`Identity`, `CustomerAdmin`, `Audit` modules + shared kernel), API, Worker, Migrator; central package management; warnings-as-errors; architecture tests for layering, module boundaries and the `IgnoreQueryFilters` ban |
| Tenancy (ADR-0001) | Tenant context, EF global query filters on every `ITenantOwned` entity, `SaveChanges` stamping/verification, SQL Server row-level security (filter + block predicates) driven by `SESSION_CONTEXT`, tenant-scoped storage paths, cache keys and job envelopes |
| Authentication (ADR-0006) | Entra ID, Google and generic OIDC providers configured as data per tenant; BFF cookie session; PKCE; antiforgery for writes; absolute and idle session limits; provisioning rules; tenant taken only from server-side configuration |
| RBAC / SoD | Permission catalogue, roles, tenant-wide or per-legal-entity assignments, permission policies, `MakerCheckerGuard`, `DifferentPersonRule`, conflicting-permissions matrix enforced at role assignment |
| Audit (ADR-0003) | Append-only, per-tenant SHA-256 hash chain written in the same transaction as the business change; DB role DENY + trigger; chain verifier, external anchoring (file stand-in), Worker job, query API with entity scoping |
| Observability / API | Serilog JSON logs with tenant/user/correlation ids, OpenTelemetry (OTLP when configured), health checks, URL-segment versioning, OpenAPI, RFC 7807 with stable error codes, rate limiting, security headers |
| Frontend | React + TS (Vite), English/Arabic with RTL, Hijri/Gregorian and SAR formatting, sign-in, home and audit-trail pages |
| Delivery | Dockerfiles and compose, GitHub Actions CI, Bicep skeleton, ADR-0006 and ADR-0007, README |

## Acceptance criteria

| Criterion | Evidence |
|---|---|
| Cross-tenant isolation tests pass | `TenantIsolationTests` (11): read/write/update/delete across tenants fail with and without the app filter (raw SQL under RLS); `TenantScopedInfrastructureTests` (storage, cache, jobs); `AuditTests` (audit trail isolation); `RbacTests`; `AuthenticationTests` (tenant claim in external token ignored) |
| Audit events cannot be updated or deleted | `AuditTests`: the application login gets permission-denied on UPDATE, DELETE and TRUNCATE; even the owner is stopped by the trigger; atomic with business changes; tampering by a privileged attacker (edit, delete, truncate tail, fully recomputed chain) is detected by the verifier and anchors |
| CI is green | All suites pass locally; workflow added in `.github/workflows/ci.yml`. CI on GitHub: see the final status in the session summary |

Test counts (all passing): unit 54, architecture 6, integration 35 (real SQL Server via Testcontainers),
API 34, web 18 (Vitest) + 4 (Playwright). `dotnet build -c Release` has 0 warnings; NuGet and npm audits report no
vulnerable packages.

## Decisions that differ from or extend the plan

- Session cookie is `SameSite=Lax`, not `Strict` (the return from the IdP is cross-site); writes need an antiforgery
  header instead (ADR-0006).
- Refresh tokens are not used: sessions slide and expire; no provider tokens are kept (ADR-0006).
- Unsupported API versions return a problem-details 404 rather than 400 (URL-segment routing).
- Audit write path is a `SaveChanges` interceptor over a shared buffer, so any module's DbContext writes audit rows
  in its own transaction (ADR-0007).

## Known limitations and follow-ups

- **Docker images not built in this sandbox**: the container's TLS-inspecting proxy is not trusted inside
  `docker build`, so NuGet restore fails there. The Dockerfiles and compose file are unverified; the equivalent
  steps (migrator, API with the restricted login, `/health/ready`) were run natively against a SQL Server container
  and worked. CI should build the images in Phase 8.
- Audit anchoring uses write-once files as a stand-in; Azure Blob immutability / S3 Object Lock comes in Phase 8.
- Client secrets are resolved from configuration (`Secrets:<ref>`); the Key Vault configuration provider is wired
  at deployment (Phase 8). Rate limiting is per instance (in memory).
- No provider-management API/UI yet (Phase 2 customer administration). Changing a provider needs a scheme eviction.
- Storage (`IFileStore`) has the tenant-scoped decorator and an in-memory backend; Azure Blob and S3 backends are Phase 2.
- `TRUNCATE` or trigger removal by the database owner is blocked only by permissions on the app login; the hash chain
  and anchors detect it (tested) but do not prevent it.
- One lint warning remains in the audit page (`set-state-in-effect`); it is a false positive for an async fetch.
- Bicep deploys only supporting resources (monitoring, Key Vault, storage, Service Bus); compute and SQL come with
  the production IaC in Phase 8.

## Open items for you

1. Approve Phase 1, or list changes.
2. Confirm `SameSite=Lax` plus antiforgery (ADR-0006).
3. Say "start Phase 2" when ready; I will show a plan first.
