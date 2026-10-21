# VPMS: Vendor Payment Management System

Independent, multi-tenant SaaS for global customers. Not part of any internal system.
Handles sensitive financial instructions: correctness, auditability and tenant isolation come before speed.

## Tech stack (fixed)
- Backend: ASP.NET Core 10 Web API (.NET 10 LTS), Clean Architecture, modular monolith
- Frontend: React + TypeScript (Vite), full RTL/Arabic support, i18n for all strings
- Database: SQL Server / Azure SQL; ORM: EF Core 10 (migrations in source control)
- Auth: Microsoft Entra ID, Google, generic OpenID Connect
- Storage: abstraction over Azure Blob and S3-compatible
- Workflows: own versioned, data-driven workflow engine
- Email: Microsoft Graph + transactional provider behind an interface
- Background: durable queues + .NET Worker Services
- ERP: Dynamics 365 Finance first, behind IErpAdapter
- Cloud: Azure-first, portable (no hard lock-in); IaC with Bicep or Terraform; Docker support

## Modules
Vendor Registration, Vendor Self-Service Portal, Invoice Management (manual/email/document, OCR-ready),
Invoice Verification (duplicates, PO matching, tax/amount), Approval Workflow (multilevel, conditional,
sequential, parallel), Delegation & Escalation, Payment Requests, Payment Proposals (group by vendor,
due date, company, currency), Treasury Controls, ERP Integration, Reporting, Audit & Compliance,
Notifications (email, in-app, Teams-ready, mobile-ready), Customer Administration.

## Tenancy and multi-company
- A tenant (e.g. a holding group) contains multiple legal entities.
- Each legal entity has its own vendors, financial dimensions, approval authorities, invoice data, ERP company mapping.
- The same vendor may serve several entities; each entity keeps its own accounting rules and payment records.
- Isolation at every layer: EF global query filters, tenant-scoped storage paths, cache keys, background jobs.
- Automated tests must prove cross-tenant access fails.

## Mandatory financial controls (never optional)
- Maker-checker; self-approval blocked
- Two-person verification for vendor bank-detail changes
- Independent invoice approval and payment release (segregation of duties)
- Re-approval when approved invoice data changes materially
- Duplicate invoice detection and ERP reconciliation
- Immutable, append-only audit history
- Encrypted financial data at rest, column-level encryption for sensitive fields
- v1 approves and PREPARES payment instructions only. NEVER transfer money. Bank execution is a separate future integration.

## ERP rules
- Only documented Dynamics 365 Finance interfaces (vendor invoice headers, lines, attachments, data entities/OData).
- NEVER write directly to the D365 database.
- All ERP logic behind IErpAdapter. Integrations must be idempotent, retryable, reconcilable.

## Localization
- Saudi support: VAT, Arabic UI and documents, RTL, Hijri/Gregorian dates, applicable ZATCA requirements.
- Receiving a supplier invoice is not issuing a tax invoice. Do not assume VPMS must issue ZATCA e-invoices.
  Document which obligations depend on VPMS's actual role; keep ZATCA a configurable, isolated module.

## Engineering standards
- Security: OWASP ASVS L2, least privilege, secrets in Key Vault, none in the repo
- Controls aligned with ISO/IEC 27001:2022 and SOC 2 expectations
- Observability: structured logging, OpenTelemetry, health checks
- API: versioned REST, OpenAPI, RFC 7807 problem details
- Tests: unit, integration (Testcontainers), API; CI on every PR
- Every feature ships with tests and updated docs
- Record hard-to-reverse decisions as ADRs in /docs/adr

## Working rules
- Work one phase at a time; follow /docs/roadmap.md. Show a plan first, then build.
- Ask before hard-to-reverse decisions. Prefer simple, well-tested designs.
- Do not start the next phase until told.
