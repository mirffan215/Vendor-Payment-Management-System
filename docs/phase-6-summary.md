# Phase 6 summary: ERP / Dynamics 365 Finance

Status: complete. Phase 7 was started on the owner's instruction ("start Phase 7") after this phase.

## What was built
- **`ErpIntegration` module** (schema `erp`): `IErpAdapter` (capability-based, typed outcomes), `MockErpAdapter` (development/tests only) and
  `D365FinanceAdapter` (documented OData, Entra client credentials). A shared contract suite runs against both.
- **Configuration:** the tenant's connection and each legal entity's mapping (company, journal, posting profile, payment-terms and tax-code maps,
  financial-dimension mappings) are versioned and every version needs a different person to approve (`Erp.Configure` / `Erp.ApproveChange`).
  Only the *name* of the secret is stored; it is read through `ISecretReader` (`Secrets:{name}`, Key Vault in production).
- **Outbound queue:** outbox with a unique idempotency key per business event, leases, exponential backoff with jitter, dead letter with audited
  retry and "handled by hand", append-only attempt history. Vendors go before their invoices; changed subjects are retired, not sent.
- **Inbound:** content-addressed inbox (replays change nothing); purchase orders feed the existing import; invoice status and vendor payments are
  stored and shown. ERP data never edits an approved invoice or causes a payment.
- **Reconciliation:** six kinds of difference (missing, amount, payee, status, unknown in ERP, paid in ERP but not released here), stable items,
  reasons required, a second person to accept an amount or payee difference, CSV export.
- **Screens (English/Arabic, RTL):** connection and mapping with a change inbox, sync monitor, reconciliation.
- ADR-0019 (queue, idempotency, reconciliation) and ADR-0020 (D365 adapter and what was not verified); architecture, data model, roadmap, risks.

## Acceptance evidence
| Requirement | Evidence |
|---|---|
| Replayed messages create no duplicates | `ErpOutboundTests`: same message twice, replay after a lost answer, four workers racing for one message (one ERP call); unique key; inbox replay tests |
| Reconciliation flags planted mismatches | `ErpReconciliationTests.Every_kind_of_planted_mismatch_is_found_exactly_once` (all six kinds), rerun creates no duplicates, fixed differences clear |
| Idempotent, retryable, reconcilable | backoff, dead letter, retry/resolve, supersede tests; adapter contract suite on both adapters |
| Secrets never exposed | stored row, API output, test result and audit entries asserted free of the secret; only a vault name is accepted |
| Tenant isolation | queue, inbox, configuration and reconciliation cross-tenant tests |

Final local results (Release): unit 601, architecture 9, integration 415 (5 skipped, the usual MinIO tests), API 121, web Vitest 93,
Playwright 3 new specs. CI: all earlier runs green except one flaky ordering assertion (equal fake-clock timestamps) which is fixed.

## Not verified, and limits
- **No test has run against a real Dynamics 365 Finance environment.** The stub server is written from the documented OData behaviour. Entity names
  (configurable), field names (fixed in the adapter), required fields, number sequences and whether a journal posts automatically all need a sandbox
  spike with the first customer before the first live posting (risk R6 raised to High until then; ADR-0020).
- The posted-invoice listing used for status and reconciliation is the least certain mapping.
- No notifications yet when messages are dead-lettered or differences are found (Phase 7).
- Earlier limits unchanged: Graph not tested live, no OCR engine, MinIO / Docker image builds, anchoring, key ring.

## Open items for you
1. Provide a D365 sandbox (or the version and posting route, Q13) so the spike can be done.
2. Say who should be alerted about dead letters and reconciliation differences (Q14).
