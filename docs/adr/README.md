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
