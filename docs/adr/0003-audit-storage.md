# ADR-0003: Audit storage and immutability
Status: Accepted
Date: 2026-10-21

## Context
Audit history must be immutable and append-only, and tampering must be detectable, including by someone with
database access. Audit writes must be consistent with the business change.

## Decision
- Store audit events in a dedicated append-only table `audit.AuditEvent` in the main database, written in the same
  transaction as the business change.
- Hash chain per tenant: `Hash = SHA-256(PrevHash || canonical-JSON(event))`, with `(TenantId, Sequence)` unique.
- Enforce append-only with: INSERT/SELECT-only DB role for the application, DENY UPDATE/DELETE, and triggers that
  raise on update/delete. Schema changes use a separate migrator identity.
- A Worker verifies chains on a schedule and anchors the latest hash daily to immutable external storage (Blob
  immutability policy or S3 Object Lock). Verification failures raise a critical alert.
- Payloads are redacted: no clear-text bank numbers or secrets, only masked values or hashes.
- Retention and deletion policy per tenant is defined in Phase 8; deletion of audit data is by policy-controlled
  partition archival, never row edits.

## Alternatives considered
- SQL Server Ledger tables: built-in tamper evidence and a good candidate; availability on Azure SQL and
  portability to other SQL Server hosts must be confirmed. The `AuditPort` hides the storage, so Ledger can replace
  the hand-rolled chain later.
- Separate event store or WORM-only store: stronger isolation, but loses same-transaction consistency.

## Consequences
- Same-transaction writes make audit gaps impossible for committed changes.
- Per-tenant chain serialisation limits write concurrency for hot tenants; mitigated by chaining per tenant and
  batching within a transaction.
- Anchoring gives evidence that survives a database compromise.
