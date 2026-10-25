# Tenant offboarding

Design: [ADR-0024](../adr/0024-retention-legal-hold-and-offboarding.md). Operator commands are subcommands of the migrator, run as a Container Apps job execution
(`az containerapp job start -g <rg> -n vpms-<env>-migrator --args "<command>" ...`), so they use the migrator's managed identity and are audited.

1. **Agree** in writing: the exit date, the export the customer needs, and that shredding is irreversible. Check there is no legal hold (`Retention → Legal holds`) and that retention periods have passed or the customer accepts
   early destruction of the records that the law requires them to keep.
2. **Suspend:** `suspend-tenant --slug <slug> --reason "<ticket>"`. Sign-in is refused; background jobs skip the tenant; data stays. Reversible with `resume-tenant --slug <slug> --reason "<ticket>"`.
3. **Export:** before suspending (or resume briefly), the tenant administrator downloads what they need: reports (CSV/Excel/PDF), the audit **evidence bundle** for the full period, the documents. A bulk export tool is not built; large tenants need a
   one-off script written and reviewed for the customer (open item).
4. **Wait** the cooling-off period after suspension: the command refuses earlier than `--min-days` (default 30). Lowering it is an operator decision that must be written in the ticket; the command also refuses while a legal hold is active.
5. **Shred:** `shred-tenant --slug <slug> --confirm <slug> [--min-days 30]`. It destroys the tenant's data keys (tombstone, never recreated), deletes the tenant's files (blob prefix), and records the action. Encrypted columns become unreadable.
   Unencrypted operational columns (names of vendors, invoice numbers) remain in the database until the customer's data is deleted in a later step; plan a purge when the financial-records disposal is decided (Q16).
6. **Keep:** the audit chain head, the anchors and the shred event, so the history that the tenant existed and when it left stays provable.
7. Record the date in the rehearsal/change log and tell the customer.

Users' personal data can be removed earlier with *Anonymise* on the user in Administration → Users (`POST /admin/users/{id}/anonymise`) (names and emails replaced, history kept by id).
