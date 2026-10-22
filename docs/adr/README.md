# Architecture Decision Records

Hard-to-reverse decisions are recorded here. Status values: Proposed, Accepted, Superseded.
Phase 0 ADRs were approved by the owner and are **Accepted**.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-tenancy-strategy.md) | Tenancy strategy | Accepted |
| [0002](0002-workflow-engine.md) | Workflow engine | Accepted |
| [0003](0003-audit-storage.md) | Audit storage and immutability | Accepted |
| [0004](0004-encryption.md) | Encryption and key management | Accepted |
| [0005](0005-erp-adapter.md) | ERP adapter | Accepted |
| [0006](0006-authentication-bff.md) | Authentication: BFF cookie sessions with per-tenant OIDC | Accepted |
| [0007](0007-database-identities-and-migrations.md) | Database identities, row-level security scope and migrations | Accepted |
| [0008](0008-bank-detail-change-control.md) | Bank-detail change control | Accepted |
| [0009](0009-vendor-portal-authentication.md) | Vendor portal authentication and data isolation | Accepted |
| [0010](0010-file-storage-and-scanning.md) | File storage and virus scanning | Accepted |
| [0011](0011-invoice-intake-and-ocr-boundary.md) | Invoice intake channels and the OCR boundary | Accepted |
| [0012](0012-verification-engine-and-rule-versioning.md) | Verification engine and per-entity rule versioning | Accepted |
| [0013](0013-pluggable-tax-providers-and-zatca-scope.md) | Pluggable country tax providers and ZATCA scope | Accepted |
| [0014](0014-workflow-engine-and-definition-versioning.md) | Workflow engine and definition versioning | Accepted |
| [0015](0015-approval-controls-sod-delegation-escalation.md) | Approval controls: segregation of duties, authority, delegation, escalation | Accepted |
| [0016](0016-reapproval-on-material-change.md) | Re-approval on material change | Accepted |
| [0017](0017-payment-preparation-and-treasury-controls.md) | Payment preparation and treasury controls | Accepted |
| [0018](0018-payment-instruction-format-signing-and-reveal.md) | Payment instruction format, signing and bank-detail reveal | Accepted |
| [0019](0019-erp-sync-queue-and-idempotency.md) | ERP sync queue, idempotency and reconciliation | Accepted |
| [0020](0020-d365-adapter-mapping-and-limits.md) | Dynamics 365 Finance adapter, mapping, secrets, and what was not verified | Accepted |
| [0021](0021-reporting-and-export.md) | Reporting, export formats and audit evidence bundles | Accepted |
| [0022](0022-notifications.md) | Notifications: sweep, outbox, channels and mandatory kinds | Accepted |
| [0023](0023-step-up-sessions-and-security-logging.md) | Step-up authentication, ending sessions, and security logging | Accepted |
| [0024](0024-retention-legal-hold-and-offboarding.md) | Retention, legal hold, disposal and tenant offboarding | Accepted |

## Template

```
# ADR-NNNN: Title
Status: Proposed | Accepted | Superseded by ADR-XXXX
Date: YYYY-MM-DD

## Context
## Decision
## Alternatives considered
## Consequences
```
