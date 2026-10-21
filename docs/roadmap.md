# VPMS Roadmap

Work one phase at a time. Each phase begins with a plan shown for approval and ends with a summary; the next
phase starts only when told. Phases match `/prompts/phase-N-*.md`.

## Definition of done (every phase)
- Code, unit/integration/API tests, and updated docs shipped together; CI green on the PR.
- Hard-to-reverse decisions recorded as ADRs in `/docs/adr`.
- No secrets in the repo; new endpoints versioned, documented in OpenAPI, and errors as RFC 7807.
- Tenant isolation and audit coverage verified for every new entity and action.
- All new UI strings go through i18n (English and Arabic) and render in RTL.

## Phase 0: Architecture and design (this phase)
Scope: `architecture.md`, `data-model.md`, ADRs 0001-0005, `roadmap.md`, `risks-and-open-questions.md`. No code.
Acceptance: documents reviewed and approved by the owner; open questions answered or accepted.

## Phase 1: Foundation (`phase-1-foundation.md`) — complete, see [phase-1-summary.md](phase-1-summary.md)
Scope: Clean Architecture scaffold, React + TS app, Docker, CI, IaC skeleton; multi-tenancy (resolution, query
filters, scoped storage/cache); authentication (Entra ID, Google, generic OIDC); RBAC and SoD primitives; audit
foundation; logging, OpenTelemetry, health checks, problem details, OpenAPI; i18n English/Arabic with RTL.
Depends on: Phase 0 approval.
Acceptance: cross-tenant isolation tests pass; audit events cannot be updated or deleted; CI is green.

## Phase 2: Customer administration and vendors (`phase-2-customer-and-vendors.md`)
Scope: tenant setup, legal entities, branding, roles, approval-matrix data model; vendor registration and
onboarding; bank details with two-person verification; vendor-entity relationships; vendor self-service portal;
storage abstraction (Azure Blob + S3), virus-scan hook, encrypted fields.
Depends on: Phase 1.
Acceptance: a bank-detail change cannot take effect without a second, different approver; vendor users only
see their own data; tests cover all of it.

## Phase 3: Invoices (`phase-3-invoices.md`)
Scope: intake (manual, portal, email via Graph, document upload, OCR-ready interface); header, lines,
attachments, lifecycle; verification engine (duplicates, PO matching, tax/amount) configurable per entity;
exceptions queue; VAT with Saudi rules as a pluggable module.
Depends on: Phases 1-2.
Acceptance: duplicates are blocked or flagged; validation rules are configurable per entity; full test coverage
of rules.

## Phase 4: Workflow and approvals (`phase-4-workflow-approvals.md`)
Scope: versioned data-driven engine (sequential, parallel, conditional, multilevel); approval matrices per entity;
version pinning; maker-checker and self-approval prevention; delegation, deadlines, reminders, escalation via
durable jobs; re-approval on material change; approver UI.
Depends on: Phases 1-3.
Acceptance: simulation tests for each workflow pattern; self-approval impossible; delegation and escalation tested.

## Phase 5: Payments and treasury (`phase-5-payments-treasury.md`)
Scope: payment requests from approved invoices; payment proposals grouped by vendor, due date, entity, currency;
treasury controls with separate approval and release; prepared, signed-off payment instruction (no transfers);
release-time re-check of bank details and invoice integrity.
Depends on: Phases 2-4.
Acceptance: the same user cannot approve an invoice and release its payment; a changed invoice invalidates its
proposal; everything is audited.

## Phase 6: ERP / Dynamics 365 Finance (`phase-6-erp-d365.md`)
Scope: `IErpAdapter` and D365 implementation (documented interfaces only); entity and dimension mapping with
Key Vault secrets; idempotent retryable sync with dead-lettering; reconciliation and manual resolution; mock adapter.
Depends on: Phases 3-5.
Acceptance: replayed messages create no duplicates; the reconciliation report flags planted mismatches.

## Phase 7: Reporting, notifications, localization (`phase-7-reporting-notifications-localization.md`)
Scope: reports (aging, approval delays, forecast, exceptions) with Excel/PDF/CSV export; audit evidence export;
notifications (Graph email + transactional provider, in-app, Teams-ready, push abstraction) with templates and
preferences; full Arabic/RTL, Arabic templates, Hijri/Gregorian, SAR and multi-currency; Saudi VAT and the
isolated ZATCA module scoped to VPMS's documented role (receives and pays supplier invoices; does not issue them).
Depends on: Phases 1-6.
Acceptance: reports respect tenant isolation; Arabic/RTL works across all screens; ZATCA scope matches the
documented decision.

## Phase 8: Hardening (`phase-8-hardening.md`)
Scope: OWASP ASVS L2 security review; dependency/container scanning and SBOM; performance and load testing;
backup/restore and DR runbooks, retention and deletion policy; production IaC, environments, blue/green or slot
deployment, monitoring; ISO/IEC 27001:2022 and SOC 2 mapping; operations/admin docs.
Depends on: Phases 1-7.
Acceptance: no high/critical findings open; restore tested; runbooks reviewed.
