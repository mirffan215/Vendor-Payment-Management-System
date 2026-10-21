Read CLAUDE.md and /docs. Implement Phase 5 only. Show a plan first.

Scope:
- Payment requests from approved invoices
- Payment proposals: group payables by vendor, due date, legal entity, currency; proposal lifecycle
- Treasury controls: separate approval and release authorization by different people; segregation of duties
- Output is a prepared, signed-off payment instruction (export/file/API hand-off). Do NOT execute bank transfers.
- Re-check bank details status and invoice integrity at release time

Acceptance: the same user cannot approve an invoice and release its payment, a changed invoice invalidates its
proposal, and everything is audited.
Stop and summarise when done.
