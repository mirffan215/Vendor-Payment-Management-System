# ADR-0001: Tenancy strategy
Status: Accepted
Date: 2026-10-21

## Context
VPMS is multi-tenant SaaS for global customers. A tenant (holding group) has many legal entities. Isolation must
hold at every layer: data, storage, cache, jobs. Tenant leakage is the highest-impact risk for financial data.

## Decision
Shared database and shared schemas with a mandatory `TenantId` discriminator on every tenant-owned table,
enforced by:
1. EF Core global query filters and a `SaveChanges` interceptor (stamp and verify `TenantId`).
2. SQL Server Row-Level Security using `SESSION_CONTEXT('TenantId')` as a database-level backstop.
3. Tenant-scoped blob paths, cache keys and job messages.
4. Architecture tests banning `IgnoreQueryFilters` outside an allow-listed service; cross-tenant tests as a CI gate.

Legal entities are a second scoping level (`LegalEntityId`) inside a tenant, applied by query filters
and user entity permissions.

The data access layer takes a `ITenantDatabaseResolver`, so a large or regulated tenant can later be moved
to a dedicated database (same schema) without code changes. Dedicated databases are not built in v1.

## Alternatives considered
- Database per tenant: strongest isolation, but costly to operate and migrate at scale. Kept as an upgrade path.
- Schema per tenant: migration complexity grows with tenant count; weak benefit over RLS.

## Consequences
- Simple operations and cross-tenant analytics for the operator are possible, but only through audited admin paths.
- Isolation depends on discipline plus tests; RLS limits the damage of an application bug.
- Per-tenant data residency needs the dedicated-database path (open question).
- Performance: all indexes lead with `TenantId`.
