# ISO/IEC 27001:2022 and SOC 2 mapping

This maps what VPMS the **product** provides to ISO/IEC 27001:2022 Annex A and the SOC 2 Trust Services Criteria. It is not a certification and does not cover the
operator's organisation (policies, HR, physical security, supplier management, risk management process); those are marked **Org**. An auditor will ask for evidence
that these controls *operate* over time (logs, tickets, reviews); the repository can show design and tests only.

## ISO/IEC 27001:2022 Annex A
| Control | Provided by VPMS | Evidence |
|---|---|---|
| A.5.1-5.4 Policies, roles | Org | Admin guide describes the product roles only |
| A.5.7 Threat intelligence | Org; Dependabot and Trivy supply vulnerability input | `security.yml` |
| A.5.9-5.11 Inventory, acceptable use | SBOM per build | `security.yml` |
| A.5.15, 5.18 Access control, access rights | Role- and permission-based access, entity scopes, session revocation | Identity module, ADR-0015, `EndpointAuthorizationTests` |
| A.5.16, 5.17 Identity and authentication information | Delegated to the tenant's identity provider; vendor portal MFA | ADR-0006, ADR-0009 |
| A.5.23 Cloud services | Azure-first, portable design; Bicep | ADR-0025 |
| A.5.24-5.28 Incident management | Security events, alerts, evidence bundle, incident runbook | [incident-response.md](../operations/incident-response.md), `monitoring.md` |
| A.5.29-5.30 Continuity | Backups (35-day point-in-time, long-term), restore drill, zone redundancy option | [restore.md](../operations/restore.md), `RestoreDrillTests` |
| A.5.31-5.34 Legal and privacy | Retention policy per tenant/country, legal hold, offboarding with crypto-shredding | ADR-0024, [retention.md](../operations/retention.md) |
| A.5.35-5.36 Independent review, compliance | Org; self-assessment here, third-party test recommended | [asvs-l2.md](asvs-l2.md), FR-1 |
| A.5.37 Documented procedures | Runbooks | `docs/operations/` |
| A.6.x People | Org | |
| A.7.x Physical | Org / Azure | |
| A.8.1-8.2 Endpoint, privileged access | Org; privileged operations are audited jobs | |
| A.8.3 Information access restriction | Tenant isolation, field encryption, masked bank details | ADR-0001, ADR-0004, ADR-0018 |
| A.8.5 Secure authentication | Step-up, lockout, session timeouts | ADR-0023 |
| A.8.7 Malware protection | Upload scanning (ClamAV) | ADR-0010 |
| A.8.8 Technical vulnerabilities | Dependency and image scanning, CodeQL, Dependabot | `security.yml`, `dependabot.yml` |
| A.8.9 Configuration management | Bicep, production start-up guards | `infra/`, `ProductionGuards` |
| A.8.10-8.11 Deletion, masking | Disposal of operational data, offboarding, log redaction | ADR-0024, `LogRedaction` |
| A.8.12 Data leakage prevention | No sensitive data in logs or API output; reveal audited | tests named in the ASVS table |
| A.8.13 Backup | Azure SQL backups, storage versioning and soft delete, immutable anchors | `infra/modules` |
| A.8.14 Redundancy | Zone-redundant option for SQL and the environment, 2+ replicas | `main.prod.bicepparam` |
| A.8.15-8.17 Logging, monitoring, clocks | Structured logs, OpenTelemetry, alerts, immutable audit | [monitoring.md](../operations/monitoring.md) |
| A.8.20-8.22 Network security | Private endpoints, WAF, internal API ingress | Bicep |
| A.8.24 Cryptography | Envelope encryption, Key Vault, TLS 1.2+ | ADR-0004 |
| A.8.25-8.29 Secure development and testing | Tests per feature, CI on every PR, architecture tests, SAST | `ci.yml` |
| A.8.31 Separation of environments | dev/test/prod parameter files, approvals | ADR-0025 |
| A.8.32 Change management | Pull requests, CI, approved production deploys, expand/contract migrations | `deploy.yml` |
| A.8.33-8.34 Test data, audit testing | Synthetic data only in tests and load tests | |

## SOC 2 Trust Services Criteria
| Criteria | Provided by VPMS | Evidence |
|---|---|---|
| CC1-CC5 Control environment, risk, monitoring | Org | |
| CC6.1 Logical access | Authentication delegated; RBAC; tenant isolation; encryption | as above |
| CC6.2-6.3 Provisioning and removal | Role assignment with approval; user disable, session revocation, anonymisation | `IUserAdminService` |
| CC6.6-6.7 Boundaries, data movement | WAF, private endpoints, outbound guard, TLS | Bicep, `OutboundGuard` |
| CC6.8 Malicious software | Scanning, supply-chain checks | |
| CC7.1-7.2 Detection and monitoring | Security events, alerts | `alerts.bicep` |
| CC7.3-7.5 Incident response and recovery | Runbooks, audit, restore | `docs/operations/` |
| CC8.1 Change management | CI, approvals, blue/green with rollback | `ci.yml`, `deploy.yml` |
| CC9 Risk mitigation (vendors) | Org; SBOM and pinned dependencies | |
| A1 Availability | Health probes, replicas, backups, tested restore, alerts | `RestoreDrillTests`, Bicep |
| C1 Confidentiality | Encryption, isolation, retention and disposal | ADR-0004, ADR-0024 |
| PI1 Processing integrity | Maker-checker, SoD, duplicate detection, idempotent ERP integration, audit | Phases 4-6 |
| P (Privacy) | Retention, anonymisation, export (evidence bundle); consent and notices are the customer's | |

## What the customer or operator must add
Policies and training; access reviews; a risk register process; supplier assessments; vulnerability SLAs; business-continuity exercises on a schedule (the restore drill is repeatable but must be run); incident-communication contacts; the third-party penetration test; and, for Saudi customers, their own ZATCA and VAT compliance (VPMS receives supplier invoices and does not issue tax invoices; `docs/zatca-scope.md`).
