Read CLAUDE.md and /docs. Implement Phase 2 only. Show a plan first.

Scope:
- Customer administration: tenant setup, multiple legal entities, branding, roles, approval-matrix data model
- Vendor registration and onboarding: tax info, documents, verification status
- Banking details with two-person verification for any change (maker-checker); changes audited
- Vendor-to-legal-entity relationships (one vendor, many entities, separate accounting rules)
- Vendor self-service portal (separate auth context), invite flow, request/response messaging
- File storage abstraction (Azure Blob + S3-compatible), virus-scan hook, encrypted sensitive fields

Acceptance: bank-detail change cannot take effect without a second, different approver; vendor users only
see their own data; tests cover all of it.
Stop and summarise when done.
