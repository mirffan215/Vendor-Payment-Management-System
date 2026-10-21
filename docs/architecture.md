# VPMS Architecture

Status: Accepted (Phase 0 approved). Design only; no application code exists yet.

VPMS is an independent, multi-tenant SaaS that approves invoices and **prepares** payment instructions.
It never transfers money. VPMS does not issue invoices; it is used for the payment process only.

## 1. Solution structure

Modular monolith, Clean Architecture. Each module owns its data, its use cases, and a small public contract.

```
/src
  /BuildingBlocks            Shared kernel: Result, Entity base, TenantContext, AuditPort, Outbox, Clock
  /Modules
    /<Module>
      <Module>.Domain          Entities, value objects, domain events, invariants (no dependencies)
      <Module>.Application     Use cases (CQRS handlers), ports/interfaces, DTOs, validators
      <Module>.Infrastructure  EF Core mappings, repositories, adapters (own schema)
      <Module>.Contracts       Public interfaces and integration events other modules may use
  /Host
    Vpms.Api                   ASP.NET Core 10 Web API: auth, versioning, OpenAPI, ProblemDetails
    Vpms.Worker                .NET Worker Services: queue consumers, timers, schedulers
  /Web                         React + TypeScript (Vite), i18n, RTL
/tests                         Unit, Integration (Testcontainers), API, Architecture, Tenant-isolation
/infra                         Bicep or Terraform, Dockerfiles, compose
/docs
```

Rules, enforced by architecture tests (NetArchTest or ArchUnitNET) in CI:
- Domain depends on nothing. Application depends on Domain. Infrastructure depends on Application.
- A module may reference another module only through its `Contracts` project. No cross-module table access;
  each module uses its own SQL schema.
- Modules communicate by direct contract calls (queries) or integration events via the outbox (commands/facts).
- Only Host projects compose the dependency graph.

## 2. Bounded contexts

| Context | Owns | Publishes / consumes |
|---|---|---|
| Customer Administration | Tenant, legal entity, users, roles, approval authorities, branding, settings | `LegalEntityCreated`, role/authority queries |
| Identity & Access | Authentication providers, sessions, RBAC, SoD rules, vendor-portal auth context | Permission checks, `UserDeactivated` |
| Vendor Registration | Vendor, vendor-entity link, onboarding, documents, bank details + change control | `VendorApproved`, `BankDetailsVerified`, `BankDetailsChanged` |
| Vendor Self-Service Portal | Portal UX, invites, messaging (uses Vendor and Invoice contracts) | Calls contracts only |
| Invoice Management | Invoice, lines, attachments, intake channels, OCR pipeline interface, lifecycle | `InvoiceReceived`, `InvoiceChangedMaterially` |
| Invoice Verification | Rule sets per entity, duplicate detection, PO matching, tax/amount checks, exceptions queue | `InvoiceVerified`, `InvoiceException` |
| Approval Workflow | Definitions, versions, instances, tasks, matrices | `InvoiceApproved`, `InvoiceRejected`, `ReapprovalRequired` |
| Delegation & Escalation | Delegations, deadlines, reminders, escalation policies | Timers via Worker |
| Payment Requests | Payment request from approved invoice | `PaymentRequestCreated` |
| Payment Proposals | Grouping, proposal lifecycle, signed-off payment instruction | `ProposalReadyForRelease` |
| Treasury Controls | Release authorization, SoD checks, release-time re-validation | `PaymentInstructionPrepared` |
| ERP Integration | `IErpAdapter`, mapping, outbox/inbox sync, reconciliation | `ErpInvoicePosted`, `ErpMismatchDetected` |
| Reporting | Read models, exports (Excel/PDF/CSV), evidence bundles | Consumes events |
| Audit & Compliance | Append-only hash-chained audit events, query API | Receives from `AuditPort` |
| Notifications | Email, in-app, Teams-ready webhook, mobile-ready push, templates, preferences | Consumes events |
| Localization/Tax (ZATCA module) | VAT rules, Hijri/Gregorian, Arabic documents, receiving-side ZATCA checks | Pluggable via interface; isolated |

## 3. Tenancy model

- Hierarchy: **Tenant** (e.g. holding group) contains many **Legal Entities**.
- Every tenant-owned row has `TenantId`. Entity-scoped rows (vendor-entity link, invoice, approval authority,
  payment record, ERP mapping) also have `LegalEntityId`. The same vendor may serve several entities; each
  entity keeps its own accounting rules and payment records on the vendor-entity link.
- Tenant resolution: from the validated token (tenant claim mapped from the identity provider config), never
  from client-supplied headers alone. Vendor portal users resolve to tenant + vendor.
- Enforcement layers (defence in depth):
  1. EF Core global query filter on `TenantId` for every tenant-owned entity, plus a base-class convention
     that fails model validation if a tenant-owned entity lacks the filter.
  2. `SaveChanges` interceptor stamps `TenantId` and rejects writes whose `TenantId` differs from context.
  3. Legal-entity access: a user has a set of permitted entities; queries are further filtered by it.
  4. SQL Server Row-Level Security as a backstop via `SESSION_CONTEXT` (see ADR-0001).
  5. Blob/object paths: `/{tenantId}/{legalEntityId}/{kind}/{id}`; storage abstraction refuses paths outside context.
  6. Cache keys prefixed `t:{tenantId}:`; distributed cache wrapper enforces it.
  7. Background jobs and queue messages carry `TenantId` and re-establish `TenantContext` before any data access;
     messages without it are dead-lettered.
- `IgnoreQueryFilters` is banned outside an allow-listed, audited admin service (architecture test).
- Automated tests must prove cross-tenant reads, writes, file access, cache hits and job execution fail.

## 4. Workflow engine

Own, versioned, data-driven engine in the Approval Workflow module (ADR-0002).

- **Definition**: JSON graph of steps (schema-validated). Step types: `Approval` (single/any-of/all-of),
  `Sequential` chain, `Parallel` fork/join (all, any, n-of-m), `Condition` (amount, vendor, currency, legal
  entity, financial dimension, risk flags), `MultiLevel` (resolve approvers from the entity's approval matrix
  by amount thresholds), `Notify`, `Wait/Timer`.
- **Versioning**: a definition has immutable published versions. Editing creates a draft; publishing creates
  version N+1. An instance records `DefinitionVersionId` at start and never changes it (in-flight instances
  finish on the version they began with).
- **Instance state**: instance, tokens/active steps, tasks. State transitions are event-sourced into an
  instance history table and mirrored into the audit log.
- **Guards in the engine** (not UI): maker-checker (the submitter/maker cannot approve), self-approval blocked
  for any step the user has acted in, no duplicate approver across levels where configured, approver must hold
  authority for the entity and amount at decision time, delegates inherit only the delegator's authority and
  the audit records both identities.
- **Re-approval**: Invoice emits `InvoiceChangedMaterially` (materiality rules per entity: amount, vendor, bank
  account, tax, currency, due date beyond tolerance). The engine cancels open tasks, marks prior approval
  superseded, and starts a new instance.
- **Time**: deadlines, reminders, escalation and delegation start/end are durable timers (Section 9).
- **Testing**: deterministic simulation harness runs definitions against fake clocks for each pattern.

## 5. Audit and immutability

Details and alternatives in ADR-0003.

- Single append-only `audit.AuditEvent` table: id, tenant, legal entity, actor (user, delegate, system),
  action, entity type/id, before/after hashes and redacted diff, correlation id, timestamp (UTC), IP/device,
  `PrevHash`, `Hash`.
- Hash chain per tenant: `Hash = SHA-256(PrevHash || canonical(event))`. Writes are serialised per tenant
  chain (sequence number plus unique constraint on `(TenantId, Sequence)`).
- Immutability: application DB role has `INSERT`/`SELECT` only on the table; `UPDATE`/`DELETE` denied;
  triggers raise errors on update/delete as a second layer; migrations role is separate.
- Tamper evidence: a verifier job recomputes chains and periodically anchors the latest hash (daily) to
  immutable storage (Azure Blob immutability policy / S3 Object Lock) outside the database.
- Audit events are written in the same transaction as the business change (via the outbox-safe `AuditPort`)
  so no financial change exists without an audit record.
- Sensitive values are never stored in clear text in audit payloads (bank account numbers appear masked or
  as hashes).
- Query API: filter by entity, actor, action, time, correlation id; read-only; scoped by tenant/entity and
  gated by an audit-reader permission. Evidence export (Phase 7) produces signed/hashed bundles.

## 6. ERP adapter

Details in ADR-0005.

- All ERP logic sits behind `IErpAdapter` in the ERP Integration module. Core modules never reference D365.
- Operations (capability-based): `SyncVendor`, `CreateVendorInvoice` (header + lines), `AttachDocument`,
  `GetInvoiceStatus`, `GetVendorPayments`, `ValidateConnection`, `GetDimensionValues`.
- First implementation: Dynamics 365 Finance via **documented interfaces only**: OData data entities
  (e.g. vendor invoice header/line entities), service endpoints and document attachment APIs. **No direct
  writes to the D365 database**, ever.
- Reliability: transactional outbox -> durable queue -> idempotent handler. Idempotency key =
  `{tenantId}:{entityId}:{vpmsObjectId}:{version}`; ERP-side correlation stored on the VPMS record. Retries
  with exponential backoff and jitter; poison messages go to a dead-letter queue with an operator workflow.
- Reconciliation: scheduled job compares VPMS and ERP status/amount/vendor/dates per entity; mismatches are
  recorded, reported, and resolved through a manual resolution task with audit.
- Per-tenant connection settings; secrets (client secrets, certificates) stored in Key Vault, referenced by name.
- Mapping: legal entity <-> D365 company (dataAreaId), financial dimension mapping per entity.
- A mock adapter ships for tests; contract tests run against both.

## 7. Security model

- **Authentication**: Microsoft Entra ID, Google, generic OpenID Connect, configured per tenant. OIDC code flow
  with PKCE for the SPA via a backend-for-frontend (BFF) with HttpOnly, SameSite cookies; short-lived access
  tokens. Vendor portal is a separate authentication context (separate scheme, audience, user store link) so a
  vendor token can never satisfy an internal policy.
- **Authorization**: RBAC with fine-grained permissions (`Invoice.Approve`, `Payment.Release`,
  `Vendor.BankDetails.Approve` ...), evaluated with entity scope and amount authority. Policy-based handlers.
- **Segregation of duties** primitives (shared kernel): `MakerCheckerGuard`, `DifferentPersonRule`,
  `ConflictingPermissions` matrix (e.g. invoice approval vs payment release cannot be held by one person
  for the same item). SoD is enforced at the decision, not only at role assignment.
- **Data protection**: TLS 1.2+, TDE on the database, column-level encryption for sensitive fields (bank
  account/IBAN, tax IDs, contact data as classified) with envelope keys in Key Vault, per-tenant data keys
  (ADR-0004). Storage encrypted at rest. Secrets only in Key Vault; none in the repo (secret scanning in CI).
- **Application security**: OWASP ASVS L2: input validation, output encoding, CSRF protection for cookie flows,
  rate limiting, SSRF-safe outbound HTTP (allow-lists), upload constraints (type, size, AV scan hook), security
  headers, dependency and container scanning, SBOM.
- **Least privilege**: separate DB roles (app, migrator, audit-append), managed identities, scoped worker
  identities.
- **Compliance alignment**: controls map to ISO/IEC 27001:2022 Annex A and SOC 2 TSC; mapping table kept in
  Phase 8 documentation.

## 8. Notification design

- `INotificationChannel` implementations: email (Microsoft Graph and a transactional provider behind
  `IEmailSender`), in-app, Teams-ready webhook, mobile-ready push abstraction.
- Domain/integration events -> notification rules -> template rendering (per tenant, per locale, Arabic/RTL
  templates) -> channel dispatch via the durable queue. User preferences and per-tenant overrides are honoured;
  mandatory control notifications cannot be turned off.
- Delivery attempts are recorded; failures retry then dead-letter.
- Email intake (Graph mailbox polling/webhooks) belongs to Invoice Management, separate from outbound notices.

## 9. Background jobs

- Durable queue abstraction `IJobQueue` (Azure Service Bus default; portable adapter for others).
  Outbox pattern guarantees at-least-once publish; consumers are idempotent (inbox table with message id).
- Worker Services host: ERP sync, reconciliation, OCR pipeline, notification dispatch, timer scanning for
  workflow deadlines/reminders/escalations/delegation windows, audit chain verification and anchoring, file
  virus-scan callbacks, retention jobs.
- Timers are persisted rows (`due_at`, `tenant`, `kind`, `payload`) claimed with lease locking so multiple
  workers scale safely.
- Every job message includes `TenantId`, `CorrelationId`, and re-establishes `TenantContext`.

## 10. Payment preparation boundary

Payment Requests are created from approved invoices. Payment Proposals group payables by vendor, due date,
legal entity and currency. Treasury Controls require a **release authorization by a different person** than
the invoice approvers and the proposal creator. At release time, the system re-checks bank-detail status
(verified, no pending change) and invoice integrity (hash of approved data unchanged); a change invalidates the
proposal. The output is a signed-off payment instruction (file/API hand-off). **Bank execution is a separate
future integration; v1 never moves money.**

## 11. Mandatory financial controls: where enforced

| Control | Enforced in |
|---|---|
| Maker-checker, self-approval blocked | Workflow engine guards; shared `MakerCheckerGuard` |
| Weakening an invoice check | `VerificationRuleSet` versions: a loosening stays Pending until a different person approves (ADR-0012) |
| Two-person bank-detail change | Vendor module: `BankDetailChange` state machine requires checker != maker, plus CHECK constraint and triggers (ADR-0008) |
| Invoice approval vs release separation | Treasury `ReleaseAuthorization` + SoD matrix |
| Re-approval on material change | Invoice `MaterialityPolicy` -> `InvoiceChangedMaterially` -> new workflow instance |
| Duplicate detection / ERP reconciliation | Verification engine; ERP reconciliation job |
| Immutable audit | Audit module (Section 5) |
| Encryption at rest + column-level | ADR-0004 |
| No money movement | No bank-execution component exists in v1 |

## 12. Cross-cutting

- Observability: structured logging (Serilog -> OpenTelemetry), traces/metrics via OpenTelemetry,
  health checks (liveness/readiness, dependency checks), correlation ids on every request/job/audit event.
- API: versioned REST (`/api/v1`), OpenAPI, RFC 7807 problem details with stable error codes.
- Frontend: React + TypeScript (Vite), all strings through i18n (English, Arabic), full RTL, Hijri/Gregorian
  date support.
- Testing: unit, integration with Testcontainers (SQL Server), API tests, architecture tests, tenant-isolation
  tests; CI on every PR.
- Cloud: Azure-first, portable abstractions (storage, queue, secrets, email), IaC (Bicep or Terraform),
  Docker images for Api and Worker.
- Localization/tax: Saudi VAT and ZATCA-related logic lives in an isolated, configurable module. VPMS does not
  issue invoices; see `risks-and-open-questions.md` for the obligation analysis.

## 13. Phase 2 additions

- **New modules:** `Documents` (attachments, quarantine, scanning), `Vendors` (vendor lifecycle, documents, per-entity
  links, requests, bank details), `VendorPortal` (vendor users, invites, MFA). Each has its own schema, DbContext and
  migrations; cross-module access is through `Contracts` only (architecture tests).
- **Vendor isolation:** `IVendorOwned` + `VendorContext` add a named query filter and a write guard on top of tenancy
  (ADR-0009). Cross-vendor existence checks use the allow-listed `BeginCrossVendorCheck` scope.
- **Two session types:** staff (`__Host-vpms`, `ctx=internal`) and vendors (`__Host-vpms-vendor`, `ctx=vendor`) never
  satisfy each other's policies.
- **Storage and encryption:** `IBlobBackend` (Azure/S3) behind `TenantScopedFileStore` (ADR-0010); `IFieldProtector`
  encrypts tax numbers, registration numbers, IBANs, account numbers and TOTP secrets with per-tenant keys and
  blind indexes (ADR-0004).
- **Worker jobs:** attachment scanning and bank-change expiry join audit chain verification.
- **Frontend:** staff screens (vendors, bank-change approvals, administration) and a separate `/portal` area.

## 14. Phase 3 additions

- **New module:** `Invoices` (schema `invoice`): invoices and lines, attachments, intake (documents, email, portal), OCR
  suggestions, versioned verification settings, verification results, exceptions, purchase orders. Country tax providers are
  separate plug-in assemblies (`Vpms.Tax.Sa`), isolated by an architecture test (ADR-0013).
- **Flow:** channel -> Draft -> (person completes) -> Submit -> Received -> verification engine (pure rules over facts, entity
  settings version pinned in the record) -> Verified or Exception -> exceptions queue (accept a flag with a reason; a block needs
  the override permission and a different person). Approval workflow starts at Verified in Phase 4.
- **Worker jobs:** email intake per enabled mailbox, extraction of scanned files, verification sweep.
- **Controls added:** loosening a check needs a second person; blocks cannot be waived by the invoice's maker; vendor status
  is never waivable; invoices and verification records cannot be deleted by the application login.
- **Cross-module reads** go through contracts only: vendor status, link currency, ERP account, contact email and tax number
  (`IVendorLookup`), legal entity country (`ILegalEntityLookup`), file storage (`IAttachmentService`).
