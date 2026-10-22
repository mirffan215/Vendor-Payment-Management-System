# Phase 5 summary: payments and treasury

Status: complete, awaiting your approval. Phase 6 has not been started.

## What was built
- **Payment requests** (new `Payments` module): one per approved invoice, only when the invoice is approved for its current version,
  the vendor can be paid and has verified bank details in the invoice currency with no change pending. Each request freezes the
  invoice's integrity hash, the bank account's fingerprint and the people involved (invoice preparers, invoice approvers, bank-detail
  maker and checker, the requester).
- **Proposals:** requests grouped per legal entity, vendor, currency and due date; numbered per entity; Draft, PendingApproval,
  Approved, Released, Invalidated, Cancelled. Requests can be taken out of a draft; a cancelled proposal frees its requests.
- **Treasury controls:** preparer, approver and releaser are three different people, enforced in the role matrix and again in the
  services. The releaser also cannot be anyone who prepared, verified or approved an included invoice, or made or checked the vendor's
  bank details. Every refusal is audited, and every release attempt (allowed or refused) is kept as a release authorisation.
- **Re-checks** at submit, approval, release and by a worker sweep: invoice unchanged and still approved, vendor still payable, bank
  details still the same verified account. A change invalidates the request and its proposal (other requests are freed); a pending bank
  change only holds approval/release back.
- **A changed invoice invalidates its proposal at once**, and once an instruction exists the invoice can no longer be revised.
- **Instruction:** CSV and JSON behind `IInstructionExporter`, encrypted at rest, SHA-256 hash and tenant-keyed signature, full bank
  details revealed only at release through an audited call. Download needs its own permission, is audited, and records the hand-off.
  Tampering is detected. **No bank execution, no execution status.**
- **UI (English/Arabic, RTL):** payable invoices and requests, proposals, proposal detail with checks, approve / send back / release /
  cancel, release attempts with reasons, instruction download and signature check.
- ADR-0017 (controls) and ADR-0018 (format, signing, reveal); architecture, data model, roadmap, README and risks updated.

## Acceptance evidence
| Requirement | Evidence |
|---|---|
| The same user cannot approve an invoice and release its payment | Role matrix (`TreasuryTests.The_role_matrix_keeps...`); service refusal for invoice approver, creator, proposal maker, proposal approver, bank maker, bank checker with all six forced into the releaser role (`Nobody_who_took_part_can_release...`); unit tests of `ReleaseSeparation` |
| A changed invoice invalidates its proposal | `PaymentProposalTests.Revising_an_invoice_invalidates_its_whole_proposal_at_once...` (eager), release and approval re-checks (stale hash, changed account, blocked vendor), sweep test, released invoice unchangeable |
| Everything is audited | `Every_step_from_request_to_hand_off_is_on_the_audit_trail_in_order_with_who_did_it`; refusals `sod.payment.blocked`; reveal, download, tampering events; append-only tables tested |
| Bank details re-checked at release | `Release_re_checks_the_bank_details...`, `A_pending_bank_change_only_holds_the_release_back...` |
| Never transfers money | No such code or status exists; instruction is Prepared then HandedOff |

Local results: unit 520, architecture 9, integration 371 (5 skipped, the same MinIO tests), API 112, web Vitest 76, Playwright
(new payments spec) 1; Release build with 0 warnings. One integration test ordered two attempts by a timestamp that a fake clock
left equal; the attempts are now ordered deterministically.

## Decisions that differ from or extend the plan
- Proposal approval is one permission step (as you chose); it can move onto the workflow engine later if customers need levels.
- Items of a draft proposal can be deleted (audited); everything else in the payment schema is never deleted.
- A bank change awaiting a decision *blocks* rather than invalidates, so a rejected change does not force re-preparation.
- Signatures are tenant-keyed hashes (tamper evidence), not asymmetric signatures; who authorised is the release authorisation.

## Known limitations and follow-ups
- Generic CSV+JSON only; bank formats (pain.001, SAMA) need a decision on the bank and country profile (Q6).
- Single currency per proposal; no FX, no withholding tax deduction, no partial payments or credit notes.
- No notifications to approvers/releasers yet (Phase 7), and no reporting.
- Reconciling what was actually paid with the ERP is Phase 6.
- Earlier-phase items unchanged: Graph not tested live, no OCR engine, MinIO / Docker image builds, anchoring, key ring.

## Open items for you
1. Approve Phase 5, or list changes.
2. Confirm a minimum of three people for payments (preparer, approver, releaser) plus invoice approvers who are none of them.
3. Confirm that a pending bank change should hold a payment back (not invalidate it).
4. Which bank file format do you need first (pain.001, a SAMA format, or the bank's own)?
5. Say "start Phase 6" when ready; I will show a plan first.
