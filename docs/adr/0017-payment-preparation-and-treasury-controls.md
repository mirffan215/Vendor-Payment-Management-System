# ADR-0017: Payment preparation and treasury controls
Status: Accepted
Date: 2026-10-22

Builds on the Phase 0 payment boundary (architecture section 10) and ADR-0015.

## Context
The step from "approved invoice" to "payment instruction" is where fraud is attempted: changing an invoice or a bank account after
approval, or one person pushing a payment through alone. CLAUDE.md requires independent invoice approval and payment release, and
says v1 prepares payment instructions and never transfers money.

## Decision
- **Payment request.** One per approved invoice, created only when the invoice is `Approved` for its *current* version, the vendor can
  be transacted with, and verified bank details exist in the invoice currency with no change pending. It freezes: the invoice's
  integrity hash (identity, entity, vendor, number, total, currency, due date, version, approved version), the bank-details id and
  fingerprint, and the people involved (invoice preparers, invoice approvers, bank-detail maker and checker, the requester). An invoice
  has at most one open request (database-enforced).
- **Proposal.** One proposal per legal entity, vendor, currency and due date, numbered per entity. Draft -> PendingApproval ->
  Approved -> Released, or Invalidated / Cancelled. Request creation, proposal creation and submission are `Payment.Prepare`; approval
  is `Payment.Approve`; release is `Payment.Release`. The role matrix forbids holding Approve with Prepare or Release, Prepare with
  Release, and Invoice.Approve with Release. Approval of a proposal is a single permission-checked step, not a workflow definition.
- **Separation is enforced in the services as well as the matrix.** The approver must differ from the proposal's creator and
  submitter and must not be anyone involved in its requests. The releaser must differ from creator, submitter and approver and from
  everyone involved (so never an approver or preparer of an included invoice, nor the maker or checker of the bank details). Every
  refusal is audited (`sod.payment.blocked`); a refused release is also stored as a Denied `ReleaseAuthorization`.
- **Re-checks.** At submit, approval and release (and by a worker sweep) each request is compared with what it froze: invoice exists,
  is payable, has the same hash; vendor can still be paid; the bank details are still the same verified account. Findings either
  *invalidate* (invoice or account changed, vendor blocked) or only *block* (a bank change awaiting a decision, which may be rejected).
  An invalidation voids the request and its proposal; the proposal's other requests return to the pool. The record of the refusal is
  saved before the error is returned.
- **A changed invoice invalidates its proposal immediately**: the invoice module calls `IPaymentLifecycle` on every revision, and once
  a payment instruction exists the invoice can no longer be revised (`payment_released`). The sweep is the backstop.
- **Release** is atomic: authorisation row, instruction, request and proposal state in one save, with row-version concurrency so two
  simultaneous releases produce one instruction.
- **No execution.** There is no status for sending, paying or settling. The instruction is Prepared, then HandedOff when the first
  authorised person downloads it. Bank execution is a separate future integration.
- **Append-only history.** Requests, proposals, instructions and authorisations cannot be deleted by the application login;
  authorisations cannot be updated. Only the items of a draft proposal can be removed (audited).

## Consequences
- Small customers need three people (preparer, approver, releaser) plus invoice approvers who are not any of them.
- A pending bank change delays release rather than killing the proposal; an approved one kills it (re-prepare).
- Currency is not converted; a proposal is single-currency.
