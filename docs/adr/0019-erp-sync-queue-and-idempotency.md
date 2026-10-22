# ADR-0019: ERP sync queue, idempotency and reconciliation
Status: Accepted
Date: 2026-10-22

## Context
ADR-0005 fixed the shape: core modules depend on `IErpAdapter`, delivery is at-least-once, handlers are idempotent, mismatches are
reconcilable. Phase 6 builds it. Money is at stake: an approved invoice must reach the ERP exactly once in effect, a failing ERP must
not lose or duplicate anything, and a person must be able to see and account for every difference between the two systems.

## Decision
**Outbound (transactional outbox).**
- `erp.ErpSyncRecord` is the queue. One row per business event, with a unique key
  `{tenant}:{entity}:{type}:{objectId}:{version}` (invoice: the *approved* version; vendor: a hash of the fields the ERP holds). The same
  event can never be queued twice, even by two racing workers (the unique index decides; the loser moves on).
- Queueing is a sweep (`IErpOutbound.EnqueueAsync`), not a hook inside the invoice or vendor modules. Those modules therefore never
  reference the ERP; the ERP module reads them through `Invoices.Contracts.IInvoicePostingSource` and `Vendors.Contracts.IVendorErpSource`.
  The invoice sweep resumes from the latest approval time already queued (inclusive, so equal timestamps are not skipped).
- Sending leases a message (`InFlight`, five minutes, optimistic concurrency on a row version). A worker that dies leaves an expired
  lease that another worker takes over; a worker that loses its lease mid-send finds out when it saves, and the ERP call was idempotent
  anyway. Several workers can run at once.
- Every handler is idempotent in the ERP: the idempotency key travels with the document and the adapter looks for it before creating.
  A replayed message gets `AlreadyExists` and the existing reference, never a second document.
- A message is sent only if its subject is unchanged since queuing: the invoice must still be approved at the queued version with the same
  integrity hash, the vendor data must still hash the same. Otherwise the message is retired (`Superseded`) and the newer one replaces it.
- Order: vendors go before invoices. An invoice whose vendor has no ERP account yet is *deferred*, which does not use up an attempt
  (and gives up after two days). A missing connection, mapping or secret defers in the same way.
- Failures: the adapter returns typed outcomes. `Transient` retries with exponential backoff (30 s doubling to a one-hour cap, plus up to
  25 % jitter, honouring `Retry-After`) for at most eight attempts. `Rejected` goes straight to `DeadLetter`.
- `DeadLetter` is for a person. Operators (`Erp.Operate`) can retry (attempts restart) or mark it handled by hand (optionally with the ERP
  reference). Both need a reason and are audited. Every attempt is an append-only `ErpSyncAttempt` row; nothing in the schema can be
  deleted by the application login.
- If an *earlier* version of an invoice already reached the ERP, the new version is not posted: it is queued as a dead letter
  (`already_posted_other_version`), because a second document would double the liability and a correction in the ERP is a human decision.
- Coding is never guessed: an unmapped tax code, dimension or dimension value is a permanent problem with a clear code.

**Inbound (inbox).**
- A scheduled pull reads purchase orders, the status of the invoices VPMS posted, and vendor payments. Each item becomes an
  `ErpInboxMessage` whose id is built from its content, so a replayed page or the deliberate fifteen-minute overlap between pulls changes
  nothing. Purchase orders go through the existing, idempotent PO import (the ERP becomes their source; manual import stays as a fallback).
  A purchase order that cannot be applied yet (unknown vendor) stays `Failed` and is retried at every pull.
- ERP data never edits an approved invoice and never causes a payment. It is stored, shown (`IErpInboundQuery`) and compared.

**Reconciliation.**
- A run compares every invoice VPMS posted with a *full* live listing from the ERP (not the incremental inbox, so deletions show up)
  and finds six kinds of difference: missing in the ERP, different amount or currency, different payee, cancelled in the ERP, paid in the
  ERP with no payment released here, and documents in the ERP for this entity's linked vendors that VPMS did not post.
- Differences are items with a natural key (kind, subject and both values), so the same difference found again is the same item, a
  changed one is a new item, and one that disappears is `Cleared`. A run that cannot reach the ERP fails and changes nothing.
- Ending a difference needs a reason. Accepting a difference in **amount or payee** is proposed by one person and confirmed by a
  different one (maker-checker); a refusal for being the same person is audited.
- The report is exportable as CSV with spreadsheet-formula neutralisation.

**Permissions.** `Erp.View`, `Erp.Configure`, `Erp.ApproveChange`, `Erp.Operate`. Configure and ApproveChange are a segregation pair.

## Alternatives considered
- Hooking the enqueue into invoice approval: couples invoices to the ERP and risks losing the event if the second save fails. The sweep
  is self-healing and the unique key makes it safe.
- Exactly-once delivery through distributed transactions: not available across an HTTP API; idempotency is the honest equivalent.
- Letting reconciliation auto-correct either side: rejected; a person decides.
- A message broker for the queue: the outbox table is durable, transactional with the data, and fits the tenant isolation model. A broker
  can be added behind the same service later.

## Consequences
- Delivery is at-least-once; the replay, race and lost-answer cases are tests, not assumptions.
- A busy tenant is bounded by the sweep batch sizes (200 invoices, 1000 vendors per entity per cycle) and the batch of due messages.
- The sweep reads all of an entity's active vendors each cycle; fine for thousands, revisit with a changed-since query beyond that.
- Accepting a difference is deliberately slower than fixing it.
