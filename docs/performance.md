# Performance and load testing (Phase 8)

## What was measured
`loadtests/` holds k6 scenarios that run against the real API (real SQL Server in a container, all middleware, row-level security, audit chain).
A seeded tenant has **3,000 invoices**, 5 vendors and one legal entity. Three kinds of virtual user run together:

| Scenario | What it does | Mix |
|---|---|---|
| browse | invoice pages, status filter, search, vendor list, audit events, payables-aging report, unread count | read-heavy, one staff session per user |
| writes | create an invoice, then read it | all writers share one tenant, so the audit chain serialises them |
| exports | PDF, XLSX and CSV export of the invoice list | 2 users |

Thresholds (checked by k6, `loadtests/scenarios.js`): failures < 1 %, list p95 < 600 ms, audit p95 < 800 ms, report p95 < 2 s, write p95 < 1.5 s, export p95 < 8 s.

## Results (2026-10-25, one 4-core / 16 GB machine running k6, the API and SQL Server together)
| Profile | Users (browse / write / export) | Failures | List p95 | Audit p95 | Report p95 | Write p95 | Export p95 | Thresholds |
|---|---|---|---|---|---|---|---|---|
| load | 20 / 10 / 2 | 0 % of 10,163 | 127 ms | 151 ms | 56 ms | 173 ms | 154 ms | all met |
| stress | 60 / 30 / 4 | 0 % of 15,123 | 548 ms | 585 ms | 237 ms | **4.19 s** | 4.13 s | write threshold crossed |

Reading the stress row: nothing failed and reads stayed inside their limits, but 30 users writing on **one tenant** queue behind each other at the audit chain head
(each write appends to a hash chain, which must be in order), and the machine was also running the load generator and the database. Writes degraded first, exports
second. This is expected from the design (ADR-0003) and is a capacity note, not a defect: one tenant is unlikely to sustain 30 simultaneous invoice writers, and
other tenants have their own chains. If a customer does, the options are batching appends per request or sharding the chain per legal entity (not built).

## What changed because of the investigation
Reading the code before measuring found five suspects; with 3,000 invoices none was a visible hotspot (before and after numbers were within noise), but they scale with data, so they were fixed:
- Invoice list: index `IX_Invoice_TenantId_CreatedAt_Id` for the default sort.
- Notification de-duplication: index `IX_Notification_TenantId_DedupeKey` matching the lookup.
- Mark-all-read: one `UPDATE` instead of loading rows.
- Template resolution: preloaded once per sweep instead of a query per event.
- Audit chain verification: anchors fetched in one batch instead of one per head.
`PerformanceIndexTests` asserts the two indexes exist and are used by the query plan.

## Caveats
- Single machine, shared by generator, API and database; numbers are indicative, not a capacity promise. Expected production scale (open question Q12) is still unknown.
- Search is still `LIKE '%text%'`; at hundreds of thousands of invoices per tenant it needs a computed prefix column or full-text search. Not needed at the sizes measured.
- Offset paging is used for lists; deep pages cost more than early ones. Keyset paging is the next step if users page deeply.
- Reports are materialised in memory with a 20,000-row cap; larger ranges need the streaming export or aggregation in SQL.
- ERP outbound (Dynamics 365) was not load-tested: it needs a sandbox (open item).
- Azure SQL service tiers behave differently from a container; repeat the `load` profile against the test environment after the first deployment and record it here.

## Running again
See `loadtests/README.md`. The "Performance" workflow (`.github/workflows/performance.yml`) runs the `load` profile on demand and weekly and uploads the k6 summary;
it is not part of every pull request.
