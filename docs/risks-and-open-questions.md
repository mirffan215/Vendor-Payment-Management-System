# Risks and Open Questions

Status: Accepted (Phase 0 approved).

## 1. Risk register

| # | Risk | Impact | Mitigation | Phase |
|---|---|---|---|---|
| R1 | Cross-tenant data leakage (missed filter, background job, cache, blob path) | Critical | Global filters + RLS backstop, banned `IgnoreQueryFilters`, tenant in every job/cache/path, mandatory isolation tests | 1+ |
| R2 | Segregation-of-duties bypass (same person approves and releases; delegate abuse) | Critical | Guards in engine and treasury, not UI; release-time recheck; SoD tests incl. delegation | 4, 5 (built: ADR-0015, ADR-0017) |
| R3 | Fraudulent bank-detail change (vendor impersonation) | Critical | Two-person verification, callback verification field, block release while change open, alerts | 2, 5 (built: ADR-0017) |
| R4 | Audit tampering or gaps | High | Hash chain, DENY update/delete, same-transaction writes, external anchoring | 1 |
| R5 | ERP drift or duplicate postings | High | Idempotency keys, replay tests, reconciliation job, documented interfaces only | 6 (built: ADR-0019) |
| R6 | D365 interface limits (throttling, entity availability, versions) differ from assumptions | **High until a spike is done**: the adapter has only been tested against a stub | Configurable entity names, adapter capability model, mock adapter, shared contract suite; **a sandbox spike is still required before the first live posting** (ADR-0020) | 6, before go-live |
| R7 | Key management errors (loss, rotation failure) | High | Key Vault with soft-delete/purge protection, versioned ciphertext, rotation runbook, tested restore | 1, 8 |
| R8 | OCR/extraction inaccuracy leads to wrong data | Medium | OCR output is only a suggestion; human verification; verification rules still apply | 3 |
| R9 | Arabic/RTL and Hijri handled late and inconsistently | Medium | i18n and RTL from Phase 1; RTL checks in UI tests; localization in definition of done | 1, 7 |
| R10 | Custom workflow engine complexity | Medium | Small step set, versioned schema, simulation harness, no arbitrary code in conditions | 4 (built, ADR-0014) |
| R11 | Hot-tenant audit chain contention | Low | Per-tenant chains, batch within transaction, measure in load tests | 8 |
| R12 | Scope creep toward executing payments | High | v1 prepares instructions only; no bank-execution component; ADR needed to change | 5 |
| R13 | Vendor portal compromise exposes internal data | High | Separate auth context and audience, vendor-scoped queries, rate limiting | 2 |
| R14 | Data residency and retention requirements differ by country | Medium | Dedicated-DB path in ADR-0001, per-tenant retention policy | 8 |
| R15 | The ERP credential is stolen or leaks | High | Only a vault name is stored; the secret is read at send time and held in memory; never in API, audit, queue or logs; least-privilege D365 role; rotation by changing the vault value | 6, 8 |
| R16 | Wrong coding posted to the ledger | High | Unmapped tax code or dimension is a permanent error, never a guess; mapping changes need a second person; reconciliation flags amount differences | 6 |
| R17 | Report PDF library licence (QuestPDF is free only below a revenue threshold) | Medium | Confirm the licence before general release; the exporter is behind `IReportExporter` and can be swapped (ADR-0021) | 7, before release |
| R18 | Notification channels never exercised against real Graph, SMTP or Teams | Medium | Stub-server tests only; try each channel in a test tenant before go-live; dead letters are visible and retryable (ADR-0022) | 7, before go-live |
| R19 | Notification flooding or a missed mandatory notice | Medium | Unique dedupe keys, mandatory kinds locked, dead-letter monitor; no digest yet | 7 |
| R20 | Evidence bundle mistaken for proof beyond what it is | Low | Documented: it shows the range was not changed since export and came from the tenant's key; the live chain verifier remains the control | 7 |
| R21 | Nothing has been deployed to Azure: the Bicep, the pipeline, private endpoints, alerts and Entra-only SQL are compiled and linted only | High until the first deployment | Deploy to dev, then test; run the checklist and restore drill in `docs/operations/`; record in the rehearsal log | 8 |
| R22 | MFA is not verified by VPMS (`amr`/`acr`); a customer with a weak identity-provider policy has weak staff sign-in | Medium | Document it to customers; build the per-provider check (finding FR-3) | 8 |
| R23 | No automatic disposal of financial records, vendor data or audit; no bulk export for offboarding | Medium | Retention report shows what is eligible; decide and build disposal and export (Q16) | 8 |
| R24 | Data-key rotation has no operator command or re-encryption job; old KEK versions must be kept | Medium | Finding FR-6; keep old versions enabled | 8 |
| R25 | A restore loses events between the last anchor and the incident without detection | Medium | Anchor interval is the bound; shorten if needed; restore needs an audited person's acceptance | 8 |
| R26 | No third-party penetration test | Medium | Commission one against the test environment before go-live (FR-1) | 8 |

## 2. ZATCA obligations and VPMS's role

**Decision (confirmed by the product owner): VPMS does not issue invoices. It is used for the payment process
only.** VPMS receives supplier invoices, verifies and approves them, and prepares payment instructions.

Obligations depend on the role VPMS plays:

| Role | ZATCA / tax implication | Applies to VPMS? |
|---|---|---|
| Issuing tax invoices, simplified invoices, credit/debit notes | E-invoicing generation, XML, cryptographic stamp, CSID onboarding, clearance/reporting via Fatoora | **No** (decision above) |
| Self-billing on behalf of suppliers | Issuance duties as above, with agreement requirements | **No**, unless scope changes |
| Receiving supplier invoices | No issuance duty. The customer needs valid supplier tax invoices to claim input VAT: capture supplier VAT number, invoice number/date, VAT amount, and the e-invoice QR/UUID where present; reject or flag missing mandatory data | **Yes**, as validation and data capture (configurable) |
| Archiving invoices and records | Retention of invoices and supporting records for the legally required period, in a retrievable form | **Yes**, retention policy per tenant and country |
| Paying invoices | VAT amounts and any withholding tax must be correct in the payment instruction; payment evidence feeds the customer's own VAT and WHT filings | **Yes**, data accuracy; filings themselves stay with the customer or ERP |
| Customer's VAT return and ZATCA reporting | Performed by the customer's ERP/tax tooling | **No**; VPMS may provide data exports |

Consequences for the design:
- ZATCA/VAT logic is an isolated, configurable module behind an interface (per-country plug-in). Saudi rules are
  the first plug-in. No e-invoice generation code is built.
- Verification rules (Phase 3) include optional checks: supplier VAT number format, mandatory fields, QR/UUID
  presence, VAT amount arithmetic. Each can be switched on per legal entity.
- Re-open the decision if VPMS later issues credit/debit notes, self-billed invoices, or any customer-facing tax document.
- Legal review by a Saudi tax adviser is recommended before go-live to confirm this reading (see open questions).

## 3. Open questions

| # | Question | Default assumed | Needed by |
|---|---|---|---|
| Q1 | Tenant isolation: shared DB with RLS for all tenants, or offer dedicated DB for some from v1? | Shared DB; dedicated DB is an upgrade path (ADR-0001) | Phase 1 |
| Q2 | Column encryption: app-level envelope encryption or SQL Always Encrypted? | App-level envelope (ADR-0004) | Phase 1 |
| Q3 | IaC: Bicep or Terraform? | Not yet decided (Azure-first suggests Bicep; Terraform eases portability) | Phase 1 |
| Q4 | Message broker: Azure Service Bus only, or also a portable alternative at launch? | Service Bus behind `IJobQueue` | Phase 1 |
| Q5 | Audit: hand-rolled hash chain or SQL Ledger tables? | Hash chain behind `AuditPort` (ADR-0003) | Phase 1 |
| Q6 | Which bank/payment file formats must the prepared instruction support (SAMA-compatible, ISO 20022 pain.001, CSV)? | Generic CSV+JSON built behind `IInstructionExporter` (ADR-0018); bank formats still to choose | Before bank integration |
| Q7 | Withholding tax: required in v1? | Data field on vendor-entity link only | Phase 3 |
| Q8 | Is a Saudi tax/legal adviser confirming the ZATCA reading above? | Pending | Before Phase 7 |
| Q9 | Vendor callback verification: manual process, or tracked in the system? | Reference field on change record | Phase 2 |
| Q10 | Data residency: required regions per tenant? | Single Azure region at first | Phase 8 |
| Q11 | Email intake: shared mailbox per tenant or per legal entity? | Per legal entity mailbox | Phase 3 |
| Q12 | Expected scale (tenants, invoices per month) for sizing and load tests? | Unknown | Phase 8 |
| Q13 | Which D365 Finance version and which posting route (journal posts automatically, or needs a separate posting step) does the first customer use? | Journal created; entity names configurable; field names to be confirmed in a spike | Before first live posting |
| Q14 | Who should receive an alert when messages are dead-lettered or a reconciliation finds differences? | Built: everyone holding the ERP operate permission (`Erp.Operate`) for that entity (ADR-0022). Confirm | Before go-live |
| Q15 | Is the QuestPDF licence acceptable for the intended customers, or should another PDF library be used? | Community licence assumed for development | Before release |
| Q16 | Should VPMS dispose of financial records, vendor master data, documents and (archived) audit at the end of retention, and who may authorise it? | Not built: reported only (ADR-0024) | Before go-live for customers with deletion duties |
| Q17 | Hosting region(s) and data residency per tenant? (was Q10) | Single Azure region parameter (`location`); backups geo-redundant by default, which may conflict with residency | Before the first production deployment |
| Q18 | RPO/RTO the customer expects, and the expected scale (Q12) | Design values in `docs/operations/README.md`; load test at 3,000 invoices only | Before go-live |
| Q19 | Does the first customer's identity-provider policy enforce MFA for staff, or must VPMS check it (FR-3)? | Not checked by VPMS | Before go-live |
