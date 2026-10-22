# ADR-0021: Reporting, export formats and audit evidence bundles
Status: Accepted
Date: 2026-10-22

## Context
Phases 1-6 hold the data (invoices, approvals, payments, ERP sync) in separate module databases, each with its own tenant filters and
row-level security. Users need reports they can export, and auditors need evidence they can check without trusting VPMS.

## Decision
- **A `Reporting` module with no tables.** Each source module exposes a read contract (`IInvoiceReportSource`, `IApprovalReportSource`,
  `IPaymentReportSource`) returning plain rows for an entity scope. Reporting composes them; no core module references Reporting. Tenant
  isolation is therefore the source modules' existing filters and RLS, and the scope (which legal entities a person may see) comes from
  `IPermissionEvaluator.GetScopeAsync`, as for the audit trail.
- **Reports:** payables aging, payment forecast, exceptions, approval delays, and an input-VAT register (needs `Report.Tax`). Amounts in
  different currencies are never added together; each currency is its own row or table. Row cap with a visible "truncated" flag.
- **Exporters behind `IReportExporter`:** CSV (UTF-8 with BOM so Excel reads Arabic; formula characters neutralised; invariant numbers),
  XLSX (ClosedXML; anything formula-like stored as text), PDF (QuestPDF with a bundled OFL font that covers Arabic, right-to-left layout,
  directional isolation for numbers and Latin text). Hijri companion columns follow the person's calendar preference.
- **Every export is audited** (who, report, filters, row count, SHA-256 of the file). `Report.View` and `Report.Export` are separate permissions.
- **Audit evidence bundle** (`Audit.Export`): a zip of `events.csv`, `events.json`, the chain verification for the range, nearest anchors and
  `manifest.json` (SHA-256 of every file) signed with a tenant-keyed hash (`IFieldProtector.BlindIndexAsync`, the same approach as the payment
  instruction). `verify` recomputes everything; a changed byte, a missing file or a bundle from another tenant fails. The export is itself an audit event.

## Alternatives considered
- **Reading all databases from one reporting store:** would bypass module boundaries and add a second copy to isolate. Rejected.
- **EPPlus / iText:** commercial licences with stricter terms than ClosedXML (MIT). Rejected for the xlsx path; the PDF library is the open question below.
- **Unsigned evidence zip:** an auditor could not tell a bundle from a doctored one. Rejected.

## Consequences
- **QuestPDF is licensed per company size.** The Community licence is free for small organisations and paid above a revenue threshold. The
  product owner must confirm the commercial position before general release. The swap point is `IReportExporter`; nothing else depends on it.
- The signature proves a bundle came from this tenant's key and was not changed; it does not replace the live chain verifier.
- Reports are computed on request, bounded by period and indexed columns. Scheduled reports, BI connectors and very large streaming exports are not built.
