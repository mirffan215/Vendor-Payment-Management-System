# Incident response

Roles: **incident lead** (decides, communicates), **operator** (runs commands), **customer contact**. Keep a timeline from the first minute (who, what, when, UTC).

## Triage
| Signal | Probable meaning | First action |
|---|---|---|
| Alert `audit-chain-broken` ("AUDIT CHAIN BROKEN" or "Events are missing") | Database changed outside the application, a restore, or a bug | Section "Audit chain" |
| Alert `security-events` spike | Credential stuffing, scanning, a broken integration | Query `vpms.security.events` by `type`; check source addresses in the API logs; consider tightening the WAF or rate limits |
| Alert `startup-refused` | A deployment with an unsafe setting | Read the message (it names the setting), fix the configuration, redeploy |
| Report of a wrong bank detail or payment instruction | Possible fraud | Section "Suspected fraud" |
| Report of seeing another tenant's data | Isolation failure | Section "Suspected cross-tenant exposure" (treat as critical until disproved) |

## Audit chain
1. Do not restart or "fix" anything. Confirm which tenant and sequence the log names: `ContainerAppConsoleLogs_CL | where Log_s has "AUDIT CHAIN BROKEN"`.
2. Was there a restore or failover in the window? If yes, this is the expected restore detection: follow restore.md section 2, steps 4-5.
3. If not: the database was changed outside the app. Preserve evidence (snapshot the database: `az sql db copy`, keep Log Analytics and SQL audit logs), restrict SQL admin access, and review SQL audit events for the window
   (`SQLSecurityAuditEvents`). The anchors in the immutable container show the last good state per tenant.
4. Tell the customer once the scope is known. Use the signed **evidence bundle** (Audit → Evidence) for the tenant and range to give them verifiable proof.

## Suspected fraud (bank details, payments)
1. Block: put a hold on the payment proposal or batch (do not release), and reject the open bank-detail change.
2. Preserve: export the audit trail for the vendor, invoice and payment (evidence bundle).
3. Establish who did what from the audit (actor, time, address, correlation id). Segregation of duties means two people were involved in any release; check both.
4. Disable the users involved (`/admin/users/{id}` disable) and **revoke their sessions** (`/admin/users/{id}/revoke-sessions`). Ask the customer to review the identity-provider sign-in logs.
5. VPMS never moved money; the bank instruction file was downloaded by a person with the release permission: ask the customer to check with their bank whether a file was submitted.

## Suspected cross-tenant exposure
1. Capture the request (correlation id from the response/log) and the identities involved.
2. Reproduce on test with the two tenants. Check `EndpointSweepTests` and the isolation tests for the module.
3. If confirmed: set the API to a maintenance revision (scale to zero), tell all affected customers per contract/legal duty, and treat as a security breach (notification deadlines differ by country: seek legal advice, e.g. 72 hours under GDPR; PDPL has its own rules).
4. Fix with a failing test first.

## Compromised credential or key
- **User:** disable and revoke sessions; the customer resets at their identity provider.
- **Vendor portal user:** disable the vendor user; review the vendor's bank-detail changes in the last 90 days.
- **Service secret (ERP, SMTP, webhook):** rotate the Key Vault secret (key-rotation.md), "Test connection", review outbound logs.
- **Managed identity or deployment identity:** remove its role assignments, create a new federated credential, redeploy.
- **KEK:** see key-rotation.md; escalate immediately.

## After
Blameless review within five working days: timeline, cause, what detection worked, what did not; add a test or alert; update the findings register and this runbook.
