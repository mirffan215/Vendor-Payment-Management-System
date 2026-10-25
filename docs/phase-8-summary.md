# Phase 8 summary: hardening

Status: complete. Nothing beyond Phase 8 has been started.

## What was built
| Slice | Result |
|---|---|
| 1 Security baseline | Data-protection key ring in blob storage wrapped by Key Vault; Key Vault configuration provider; production start-up guards; trusted-proxy handling; request limits; hardened SPA container |
| 2 Authorization and input | Build-failing test that every action makes an authorization decision; anonymous and role-less sweep; SSRF guard on all outbound calls; vendor child-route ownership; paging bounds |
| 3 Step-up, sessions, logging | Re-authentication for sensitive actions; session revocation; log redaction; security events and metric; shared OpenTelemetry/Serilog block in API and worker (ADR-0023) |
| 4 Supply chain | CodeQL, Trivy, CycloneDX SBOM, Dependabot, SHA/digest pinning, locked restore |
| 5 Retention and offboarding | Per-tenant/country policy with two-person shortening, legal holds, disposal of operational data, tenant suspend/resume/shred with crypto-shredding (ADR-0024) |
| 6 Backup and DR | Immutable external audit anchors, restore detection and acceptance, `verify-restore`, a restore drill against a real SQL backup |
| 7 Performance | Load host + k6 scenarios, two indexes and four query fixes, measured results (`docs/performance.md`) |
| 8 Deployment | Bicep: Entra-only SQL with PITR/LTR, Container Apps, registry, Key Vault, storage, separate immutable anchor account, private endpoints, Front Door + WAF, alerts, locks; blue/green deploy workflow with migration gate and rollback (ADR-0025) |
| 9 Documents | ASVS L2 checklist, findings register, STRIDE threat model, ISO 27001 / SOC 2 mapping, eight runbooks |

## Acceptance
| Requirement | Evidence |
|---|---|
| No high/critical findings open | [findings-register.md](security/findings-register.md): 0 Critical, 0 High open; 15 closed (6 High, 8 Medium, 1 Low) each with a test or CI check; 3 Medium and 4 Low open or accepted, each needing your decision |
| Restore tested | `RestoreDrillTests` (3 tests): real `BACKUP`/`RESTORE`, lost events detected, a person's acceptance lets the chain continue, encrypted data still reads, file references checked. **Azure restore not tried.** |
| Runbooks reviewed | `docs/operations/` written from the code and Bicep; **not rehearsed on Azure**; rehearsal log in its README. "Reviewed" here means self-review; your review is the open step |

## Final results (Release, 0 warnings)
Architecture 14, unit 734, API 169, integration 509 passed / 11 skipped (MinIO tests, no image in the sandbox) after fixing three failures found by the first full run (two S3 stub listing tests, one notification-settings test broken in slice 2); web Vitest 115, Playwright 40, `tsc` clean, oxlint shows only the three pre-existing warnings. The fixed tests were re-run on their own (13 pass); the whole integration suite was not re-run after the fixes.

## Load test (single 4-core machine, 3,000 invoices)
`load` profile (20 browse / 10 write / 2 export users): 0 % failures, list p95 127 ms, write p95 173 ms, all thresholds met. `stress` (60 / 30 / 4): 0 % failures, reads inside limits, **write p95 4.2 s** against a 1.5 s threshold because
30 writers on one tenant queue at the audit chain head. Indicative only (shared machine; expected scale Q12 unknown).

## Not verified, and limits (please read)
- **Nothing was deployed.** The Bicep compiles and lints; the pipeline YAML parses; the Entra-only SQL user creation (`CREATE USER … FROM EXTERNAL PROVIDER`), private endpoints, Front Door, alert queries, managed-identity access and blue/green traffic shifting have never run. Expect to fix property-level mistakes in the first dev deployment. The nginx image and the `FRONT_DOOR_ID` check were not run here (Docker Hub rate limit); `nginx -t` runs in CI.
- Graph, SMTP, Teams and Dynamics 365 were never run live (earlier phases). A D365 sandbox spike is still needed (R6, Q13).
- CodeQL needs GitHub Advanced Security on a private repository; the first runs of the new security workflow (CodeQL, Trivy, SBOM) are untested until CI runs.
- **No third-party penetration test** (FR-1). A self-review is not a substitute.
- **MFA assurance is not checked by VPMS** (FR-3). **Data-key rotation has no operator command or re-encryption job** (FR-6).
- **Financial records, vendor data, documents and audit are never deleted automatically**, and there is no bulk export for offboarding (Q16).
- Workflow notices lost their `DENY DELETE` so they can be disposed; this is a deliberate, recorded relaxation.
- The audit-anchor immutability policy is created unlocked; locking is irreversible and is a manual step in the deployment runbook.
- RPO/RTO are design values, not measurements on Azure.

## Open items for you
1. Decide FR-3 (build the MFA check or accept) and FR-6 (build key re-encryption or accept).
2. Decide Q16 (disposal of financial records) and Q17/Q18 (regions, residency, RPO/RTO, scale).
3. Provide an Azure subscription and an Entra tenant for the first dev deployment; then rehearse deployment, rollback and restore and fill the rehearsal log.
4. Enable GitHub Advanced Security (or drop CodeQL) and create the GitHub environments listed in `docs/operations/deployment.md`.
5. Commission a penetration test against the test environment; confirm the QuestPDF licence (Q15), the ZATCA reading with a Saudi adviser (Q8), who is alerted for dead letters (Q14).
