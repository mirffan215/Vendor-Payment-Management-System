# Phase 3 summary — Invoices

Status: complete, awaiting approval. Branch `claude/bold-wozniak-mkomqb`. Phase 4 has not been started.

## What was built

| Area | Delivered |
|---|---|
| Invoice core | `Invoices` module: invoice header and lines, attachments, status lifecycle as a state machine (Draft, Received, InVerification, Exception, Verified, ReadyForApproval, Rejected, Cancelled), shared money and invoice-number rules, entity-scoped staff API; invoices and verification records cannot be deleted by the application login |
| Intake (ADR-0011) | Manual entry; document upload; vendor-portal submission (simplified status for the vendor); email through Microsoft Graph behind `IMailboxReader` with an in-memory fake, idempotent per message, unmatched-sender queue, retries and dead-lettering; OCR-ready `IDocumentExtractor` whose output is only a stored suggestion |
| Rule settings (ADR-0012) | Fixed rule catalogue; versioned settings per legal entity (switch, Block or Flag, bounded thresholds); defaults as version 0; **loosening a check needs a second person to approve**, tightening is immediate; settings-change inbox |
| Verification engine | Pure engine over facts: exact, same-number and similar-invoice duplicates; PO required and 2-way PO match (price, cumulative quantity, value); totals; line amounts; VAT arithmetic; dates; vendor status. Every run records one result per rule with the invoice and settings versions |
| Exceptions | Queue with entity scoping; a flag is accepted with a reason; a block needs the override permission and a person who did not create or last edit the invoice; vendor status can never be waived; waivers apply to one invoice version; assignment; re-verification |
| Purchase orders | Per-entity tables with a versioned, idempotent import (until the ERP adapter in Phase 6) |
| Tax (ADR-0013) | `ITaxRuleProvider` plug-ins chosen by the entity's country: Saudi provider in its own assembly (receive-side checks only) and a standard fallback; each check is an entity switch; `docs/zatca-scope.md` |
| Worker | Email polling, extraction of scanned files, verification sweep |
| Frontend | Staff: invoice list, editor, detail with verification and exceptions, upload with suggestions, exceptions queue, rule settings with approval inbox, unmatched mail, mailbox and purchase-order import. Portal: send an invoice and follow its status. English/Arabic, RTL |

## Acceptance criteria

| Criterion | Evidence |
|---|---|
| Duplicates are blocked or flagged | `InvoiceVerificationTests` (exact duplicate blocked; same number with another amount and a near miss flagged; other entity, drafts, cancelled and rejected invoices ignored) and `DuplicateRuleTests` (formats of the same number, rounding, fuzzy boundaries) |
| Validation rules are configurable per entity | `VerificationRuleSetTests` and `InvoiceVerificationTests.The_same_data_gets_a_different_outcome_under_each_entitys_own_rules`; thresholds, severities and switches are per-entity data; loosening needs approval (unit, integration and API tests) |
| Full test coverage of rules | Table-driven unit tests for every rule (pass, fail, boundary, tolerance, rounding, multi-currency), the engine, the loosening logic, the Saudi provider and the Graph adapter; integration tests for the pipeline end to end |

Test counts (all passing locally): unit 398, architecture 8, integration 244 (5 MinIO tests skipped, see Phase 2), API 93,
web 48 (Vitest) + 9 (Playwright). `dotnet build -c Release` has 0 warnings.

## Decisions that differ from or extend the plan

- Verification runs **synchronously on submit and on edit**, with a worker sweep as a safety net, so users see the result at once.
- Incomplete drafts from intake cannot be submitted until number and total are entered (`incomplete_invoice`).
- Duplicate checks compare within the same vendor **and legal entity**, because each entity keeps its own invoice data.
- Tax checks became switches inside the existing `tax.vat` rule so they share the versioning and approval of other settings.
- Defaults are conservative: requiring the supplier tax number or e-invoice reference is opt-in per entity.
- `IVendorLookup`, `ILegalEntityLookup` and `IAttachmentService` gained narrow read methods (contact email match, ERP account,
  link currency, tax number, entity country, reassigning intake files) instead of the invoice module reaching into other modules.

## Known limitations and follow-ups

- **Microsoft Graph is not verified against a live mailbox.** The adapter is tested against a scripted HTTP handler. First use
  needs an app registration with `Mail.ReadWrite` (application) and a decision on scoping it to the invoice mailboxes.
- **No OCR engine.** The extractor interface and storage exist; the default finds nothing.
- **Two-way PO matching only**; three-way needs goods receipts from the ERP (Phase 6). PO data is imported by staff until then.
- **Fuzzy duplicate thresholds are untuned**; they default to flagging, and are per-entity data.
- **Vendor tax numbers are decrypted inside the verifier** for comparison (never shown or logged); a dedicated comparison
  method that never returns the value could replace this.
- The staff navigation still shows every link (permissions are enforced by the server), as in Phase 2.
- Not yet: credit notes and negative invoices (amounts must be zero or more), partial payments, multi-currency conversion.
- MinIO tests, Docker image builds, anchoring and key-ring items from Phases 1 and 2 are unchanged.

## Open items for you

1. Approve Phase 3, or list changes.
2. Confirm that tightening changes may apply at once while any loosening needs a second person (ADR-0012).
3. Decide whether a waived Block should also need the Verification Approver or stay with the override permission plus a different
   person (current behaviour).
4. Say "start Phase 4" when ready; I will show a plan first.
