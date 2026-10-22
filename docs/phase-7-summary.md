# Phase 7 summary: reporting, notifications, localization

Status: complete. Phase 8 has not been started and waits for the owner's instruction.

## What was built
- **Reporting** (ADR-0021): payables aging, payment forecast, exceptions, approval delays, input-VAT register; CSV, Excel and PDF (Arabic, right to left);
  entity-scoped, per-currency, audited exports. Permissions `Report.View`, `Report.Export`, `Report.Tax`.
- **Audit evidence bundle:** signed zip for a date range plus a verify endpoint that detects changed files, a bad signature, broken chain links and another tenant's bundle. `Audit.Export`.
- **Notifications** (ADR-0022): sweep over module events, per-channel outbox with retry and dead letter, in-app / email (Graph, SMTP) / Teams webhook / push abstraction,
  mandatory kinds locked, preferences, English and Arabic templates, two-person approval of channel configuration.
- **Localization:** language and calendar stored per user and applied at sign-in and in email; English and Arabic wording for all backend error codes (drift test); money through one formatter.
- **Screens:** reports, bell and inbox with preferences, notification administration, audit filters with evidence download and verification.
- **Saudi VAT:** input-VAT register only. An architecture test fails the build if issuing or ZATCA clearance concepts appear (`docs/zatca-scope.md`).

## Acceptance evidence
| Requirement | Evidence |
|---|---|
| Reports respect tenant isolation | `ReportingTests`, `ReportsApiTests`, `AuditEvidenceTests`, `NotificationTests` cross-tenant and entity-scope cases |
| Arabic/RTL across all screens | `arabic-sweep.spec.ts`: every staff route in Arabic at 360px: `dir=rtl`, no sideways scroll, no raw keys; locale parity and no-hard-coded-text tests |
| ZATCA scope matches the decision | `No_module_issues_tax_invoices_or_talks_to_ZATCA_clearance`; `docs/zatca-scope.md` |

Final local results (Release, 0 warnings): architecture 11, unit 668, integration 449 passed / 5 skipped (usual MinIO tests) / 1 failed, API 125, web Vitest 106, Playwright 39.
The one integration failure was the SQL Server container running out of file space while creating a test database; the same test passes when run alone.

## Not verified, and limits
- **Graph `Mail.Send`, real SMTP servers and Teams webhooks were never run live.** Tests use in-process stub servers. D365 is still stub-tested only (Phase 6).
- Mobile push has no device registration and sends nothing.
- QuestPDF has a revenue-based licence; confirm before release (R17, Q15).
- No digest emails, scheduled reports or BI connectors. The older `WorkflowNotice` list duplicates the new inbox and should be folded in.
- Q8 (Saudi tax adviser confirmation) is still open; Q13 (D365 version and posting route) and the D365 sandbox spike are still needed.

## Open items for you
1. Confirm the QuestPDF licence is acceptable (Q15).
2. Confirm who is alerted about dead letters and reconciliation differences (Q14; built as holders of `Erp.Operate`).
3. Arrange the Saudi tax adviser review (Q8) and a test tenant for Graph, SMTP and Teams.
