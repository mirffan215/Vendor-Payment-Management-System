# ADR-0005: ERP adapter
Status: Proposed
Date: 2026-10-21

## Context
Dynamics 365 Finance is the first ERP; others are likely later. Only documented D365 interfaces may be used, never
direct database writes. Integration must be idempotent, retryable and reconcilable.

## Decision
- Core modules depend only on `IErpAdapter` (in `ErpIntegration.Contracts`), a capability-based interface:
  vendor sync, invoice header/line creation, attachments, status retrieval, payment status retrieval,
  dimension lookup, connection validation.
- The D365 implementation uses documented OData data entities and service endpoints, authenticated with Entra ID
  app registrations; secrets referenced from Key Vault per tenant.
- Outbound flow: business change -> outbox row (same transaction) -> durable queue -> idempotent handler.
  Idempotency key `{tenant}:{entity}:{objectId}:{version}` is stored on `ErpSyncRecord` (unique) and sent as a
  correlation field where the ERP supports it; before create, the handler checks for an existing ERP record.
- Retries: exponential backoff with jitter; poison messages are dead-lettered and surfaced to operators.
- Inbound flow: scheduled pull (and webhooks/business events where available) through an inbox table with message
  id de-duplication.
- Reconciliation job compares VPMS and ERP state and records mismatches for manual resolution with audit.
- A mock adapter provides deterministic behaviour for tests; shared contract tests run against both adapters.

## Alternatives considered
- Direct SQL integration: prohibited by project rules.
- Third-party iPaaS in the middle: extra dependency and cost; not required for v1.

## Consequences
- Adding another ERP means one new adapter plus mapping UI, without touching core modules.
- At-least-once delivery means all handlers must be idempotent; tests replay messages to prove it.
- D365 interface limits (throttling, entity availability) must be validated early in Phase 6 (see risks).
