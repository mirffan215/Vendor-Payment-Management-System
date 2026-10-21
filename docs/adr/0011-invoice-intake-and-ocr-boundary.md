# ADR-0011: Invoice intake channels and the OCR boundary
Status: Accepted
Date: 2026-10-21

## Context
Supplier invoices arrive by hand entry, a document upload, the vendor portal and email. OCR will be added later and its output
is not trustworthy enough to pay on (risk R8). Email cannot be tested against a live mailbox in development.

## Decision
- All channels end in the same place: a **Draft** `Invoice` created through one service (`InvoiceService.CreateCoreAsync`), so
  the same validation, duplicate keys, audit and verification apply whatever the source. The source and the kind of creator
  (Staff, Vendor, System) are recorded.
- A file that arrives without data (document upload, email) produces an **incomplete draft**. It cannot be submitted until a
  person has entered the invoice number and a total above zero (`incomplete_invoice`).
- **OCR is a suggestion.** `IDocumentExtractor` returns fields with a confidence; the default implementation returns nothing. A
  worker job runs it only on files that scanned clean, stores the result as `InvoiceExtraction` and never copies a value into
  the invoice. A person reads the suggestion and types the value; verification rules run on what the person entered.
- **Email** is read through `IMailboxReader`: a Microsoft Graph implementation (application permissions, one mailbox per legal
  entity configured as data, secret supplied from Key Vault) and an in-memory fake. Each message is stored once
  (`IntakeMessage`, unique per entity and message id), so a replay creates nothing. Attachments are stored in quarantine first.
  A sender is matched to a vendor only when exactly one active vendor linked to the entity lists that address as its contact
  email; otherwise the message waits in an **unmatched queue** for a person. A vendor is never created from email. A message
  that fails is retried and dead-lettered after three attempts without blocking the others.
- **Vendor portal** invoices are created and submitted by the vendor session; the vendor sees a simplified status
  (Received, In review, Needs attention, Rejected, Cancelled) and never the checks or reasons behind it.

## Alternatives considered
- Auto-filling the invoice from OCR with a confidence threshold: rejected for v1; wrong amounts reaching payment is the
  worst outcome and thresholds are hard to tune without data.
- Creating vendors or guessing vendors from email domains: rejected; it is an impersonation path.
- Reading mail inside the API request: rejected; polling belongs in the worker.

## Consequences
- The Graph adapter is covered by tests against a scripted HTTP handler, not by a live mailbox; a first run in a real tenant
  needs the app registration (Mail.ReadWrite application permission, ideally scoped with an application access policy).
- Someone has to work the unmatched queue; the queue is entity-scoped and permissioned.
- A real OCR engine is a new `IDocumentExtractor` registration; nothing else changes.
