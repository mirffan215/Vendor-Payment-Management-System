# Retention and legal hold

Design and boundaries: [ADR-0024](../adr/0024-retention-legal-hold-and-offboarding.md). Screen: **Administration → Retention** (roles *Retention Administrator*, *Retention Policy Approver*).

## What the policy does
- Per tenant (and optionally per country of the legal entity) the number of years each class is kept. System defaults: financial records 7 years, supplier master data 7 years after the last transaction,
  operational notices 180 days, audit 10 years and never less than financial records.
- **Shortening** below the default needs a second person (*Retention Policy Approver*); lengthening applies immediately. A policy has versions; the previous version stays visible.
- **Legal hold** (tenant-wide, one legal entity or one vendor, with a reason) stops disposal of the covered records. A person with the hold permission places it; **a different person** releases it.

## What is deleted automatically (v1)
Only in-app notifications (with their delivery rows) and workflow notices older than their retention. The sweep runs per tenant, skips held scopes, and writes an audit event with the class, the count and the cut-off date.

## What is not deleted automatically
Financial records, supplier master data, documents and the audit trail. The **retention report** (same screen) shows what has passed its retention period, per class and entity, so the customer can decide. Disposing of financial
records is irreversible and crosses four modules and the file store: it needs its own decision (Q16), a dedicated database identity and a restore-aware design. The audit trail is never edited; it is archived behind a signed checkpoint
only when that design exists.

## Country defaults to confirm per customer
Saudi VAT record keeping is commonly six years or more (customer's adviser to confirm); other countries differ. The default of seven years is conservative for most.
