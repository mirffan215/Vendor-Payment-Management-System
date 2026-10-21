Read CLAUDE.md and /docs. Implement Phase 1 only. Show a plan first.

Scope:
- Solution scaffold (Clean Architecture), React + TS app, Docker, CI pipeline, IaC skeleton
- Multi-tenancy: tenant resolution, EF global query filters, tenant-scoped storage and cache
- Authentication: Entra ID, Google, generic OIDC; session and token handling
- RBAC and permission model with segregation-of-duties primitives (maker-checker helpers)
- Audit foundation: append-only audit events (hash-chained or equivalent tamper evidence), query API
- Structured logging, OpenTelemetry, health checks, problem-details errors, OpenAPI
- i18n setup with English and Arabic, RTL layout

Acceptance: cross-tenant isolation tests pass, audit events cannot be updated or deleted, CI is green.
Stop and summarise when done.
