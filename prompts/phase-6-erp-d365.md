Read CLAUDE.md and /docs. Implement Phase 6 only. Show a plan first.

Scope:
- IErpAdapter abstraction and the Dynamics 365 Finance implementation using documented interfaces only
  (vendor sync, vendor invoice headers, lines, attachments, journal mapping). No direct SQL writes.
- Entity mapping: legal entity to D365 company, financial dimension mapping, per-tenant connection settings
  with secrets in Key Vault
- Idempotent, retryable outbound/inbound sync via durable queues with dead-letter handling
- Reconciliation: VPMS vs ERP status, mismatch report, manual resolution workflow
- Mock ERP adapter for tests

Acceptance: replayed messages create no duplicates, reconciliation report flags planted mismatches.
Stop and summarise when done.
