# ADR-0024: Retention, legal hold, disposal and tenant offboarding
Status: Accepted
Date: 2026-10-22

## Context
VPMS keeps financial records (invoices, approvals, payment instructions, ERP history), supplier master data and documents, and an immutable audit
trail. Customers must keep financial records for a legally required period (Saudi VAT: at least six years) and must be able to delete what they no
longer need to keep, and to leave. Two design facts shape what is possible:
- Many tables deny `DELETE` to the application identity, and the audit table has a trigger that refuses any change (ADR-0003, ADR-0007).
- Personal data in encrypted columns can be made unreadable by destroying the tenant's data key (ADR-0004), but files are not field-encrypted and a
  hash-chained audit trail cannot be edited without breaking the chain.

## Decision
1. **Policy.** Each tenant has a versioned retention policy: years to keep per data class, with optional overrides per country of the legal entity.
   System defaults apply until a tenant sets its own: financial records 7 years, supplier master data 7 years after the last transaction,
   operational notices 180 days, audit 10 years (never less than financial records). A policy can only be shortened below the system default by a second
   person's approval (the same pending / approve pattern as ERP and notification settings); lengthening applies at once.
2. **Legal hold.** A hold (tenant-wide, or for one legal entity or vendor, with a reason) stops disposal of the covered records. Anyone with the hold
   permission may place one; releasing it needs a second person. Disposal checks holds immediately before it deletes.
3. **What is disposed automatically in v1:** operational data that is not a financial record: in-app notifications and their delivery rows, and
   workflow notices, once older than their retention. The sweep runs per tenant like the other jobs and audits each run (what class, how many, cut-off). Workflow notices had been made undeletable for the application role in Phase 4; they are informational copies (the escalation is in the approval history and the audit trail), so a migration lifts that one `DENY DELETE`.
4. **What is not disposed automatically in v1:** financial records, supplier master data, documents and the audit trail. A **retention report** lists
   what has passed its retention period (counts per class and entity) so the customer can see what is eligible, but nothing is deleted. Disposal of
   financial records is irreversible and spans four modules and the file store; it needs its own decision, a dedicated database identity that is
   outside the application role (the `DENY DELETE` rules stay as they are for the application), and a restore-aware design. It is left as an explicit
   open question (Q16) rather than built on assumption.
5. **Audit is never edited or deleted in v1.** It already stores pseudonymous identifiers (user ids), not names or e-mail addresses; a person's name and
   e-mail live in the user record, which can be anonymised (`AnonymiseUser`) without touching the chain. Source addresses (IP) stay in the audit trail as
   security evidence for its retention period. Archiving old audit events behind a signed checkpoint is a documented future design, not built.
6. **Offboarding** is a deliberate, staged, mostly irreversible process run from the migrator by an operator, never from the web app:
   `suspend-tenant` (sign-in and background work stop, data intact, reversible), a documented export (audit evidence bundle and report exports) and
   then, after a cooling-off period (default 30 days, configurable) and only if no legal hold exists, `shred-tenant --confirm <slug>`: the tenant's
   data keys are destroyed (status `Destroyed`, a tombstone is kept so a new key can never be silently created), its files are deleted by tenant prefix,
   and the tenant is marked `Offboarded`. The audit trail, chain head and anchors are kept. Encrypted columns become unreadable; plain columns remain
   until the database rows are removed, which for financial records follows the retention rules above. Backups taken before the shred still hold the keys
   until they expire, so the backup retention period is part of the customer's deletion promise and is stated in the runbook.

## Alternatives considered
- **Delete financial records automatically at the end of retention.** Rejected for v1 for the reasons in point 4.
- **Redact the audit trail on request.** Breaks the hash chain, which is the point of it; pseudonymous ids plus anonymising the user record gives the same
  practical result.
- **Per-tenant databases so a tenant can be dropped.** Offered by ADR-0001 as an upgrade path; not needed for v1.

## Consequences
- Customers get a clear, configurable retention statement, holds, a retention report, automatic cleanup of operational data, and a way to leave with
  cryptographic erasure of sensitive fields and deletion of files.
- Plain-text business data (names, invoice text) of an offboarded tenant stays in the database until a disposal capability exists; the runbook says so.
- Shredding keys is final: restoring an old backup into a shredded tenant brings the keys back, so restores must check the tenant register (runbook).
