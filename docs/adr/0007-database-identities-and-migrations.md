# ADR-0007: Database identities, row-level security scope and migrations
Status: Accepted
Date: 2026-10-21

## Context
ADR-0001 (tenant isolation) and ADR-0003 (immutable audit) rely on SQL Server enforcing rules even when application
code is wrong. That only holds if the application connects with a restricted identity and cannot change the schema.

## Decision
- **Two identities.** The *migrator* (database owner) applies migrations. The application and worker connect as a
  restricted login that is a member of the database role `vpms_app`. The role is created by migrations and receives
  data access per schema; the audit schema grants `SELECT, INSERT` only on events and anchors (UPDATE/DELETE denied)
  and no DELETE on the chain head.
- **Row-level security** on every tenant-owned table: a filter predicate and block predicates (after insert, before
  and after update, before delete) based on `SESSION_CONTEXT('TenantId')`. The context is set on every EF connection
  open by `TenantSessionContextInterceptor`. RLS applies to the owner as well; nothing relies on the owner being
  exempt.
- **Platform scope.** A narrow, explicit bypass (`SESSION_CONTEXT('IsPlatform') = 1`, EF filter `IsPlatformScope`)
  exists for work that is legitimately cross-tenant or happens before a tenant is known: login provider lookup,
  tenant slug lookup, audit verification and anchoring, migrations. It is entered only through
  `TenantContext.BeginPlatformScope()`, `IgnoreQueryFilters` is banned by an architecture test, and every use is
  confined to a using-block.
- **Raw connections must be opened through EF** (`Database.OpenConnectionAsync`) so the session context is applied;
  opening the underlying `DbConnection` directly would silently hide rows under RLS (this caught a real bug).
- **Per-module DbContexts and schemas** (`customer`, `identity`, `audit`, later others), each with its own
  migration history. `Vpms.Migrator` applies all of them in order and can provision the application login
  (`VPMS_APP_LOGIN`, `VPMS_APP_LOGIN_PASSWORD`). Migrations are never applied at API start-up.
- **Audit append** writes in the caller's transaction through a `SaveChangesInterceptor`, locking the tenant's chain
  head row (`UPDLOCK, HOLDLOCK`) to keep the sequence gap-free (see ADR-0003).

## Alternatives considered
- One shared DbContext for all modules: simpler, but couples module schemas and migrations.
- Database per tenant for isolation: kept as an upgrade path (ADR-0001).

## Consequences
- Integration tests run against real SQL Server (Testcontainers) using the restricted login, so permission and RLS
  behaviour is what production gets.
- A compromised owner identity can still disable the audit trigger or truncate the table; the hash chain and external
  anchors are what expose that (tested).
- Developers must use the migrator and the restricted login locally (docker compose does this).
