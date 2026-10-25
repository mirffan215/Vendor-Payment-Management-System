# Threat model (STRIDE per area)

Scope: the deployed system as designed in `docs/architecture.md`. Assets, in order of value: the integrity of approved payment instructions and vendor bank details; the
confidentiality of financial data between tenants; the audit trail; availability of the approval process; credentials and keys.

**Trust boundaries:** browser ↔ edge (Front Door/WAF) ↔ web (nginx) ↔ API (internal) ↔ SQL/storage/Key Vault; worker ↔ the same data stores and outbound ERP/mail/Teams; vendor portal users (external, less trusted than staff); tenant identity providers (trusted to authenticate, not to authorise).
**Attackers considered:** an unauthenticated internet user; a vendor user; a staff user with a legitimate role trying to exceed it; a staff user of another tenant; an insider with operator or database access; a compromised dependency or build; a stolen backup.

| Area | Threat (STRIDE) | Control | Residual |
|---|---|---|---|
| Sign-in (staff) | **S** stolen session cookie | `Secure`/`HttpOnly`/`SameSite` cookie, idle + absolute timeout, server-side revocation, step-up for sensitive actions | Cookie theft from a compromised browser within the session lifetime; step-up limits the damage to non-sensitive actions |
| | **S** weak authentication at the customer's identity provider | Delegated; VPMS trusts the provider | MFA is not verified by VPMS (FR-3) |
| | **E** forged callback / open redirect | State and nonce via the OIDC middleware; return URL allow-list (`SafeReturnUrl`); issuer pinned per provider | Low |
| Vendor portal | **S** credential stuffing | MFA challenge, lockout, rate limits, separate audience | Vendor mailbox compromise |
| | **I** a vendor sees another vendor's or staff data | Vendor-scoped queries, ownership checks, RLS | Covered by isolation tests |
| Tenancy | **I** cross-tenant read/write | EF global filters, SQL row-level security (`vpms_app` role), tenant-scoped storage/cache/jobs, `DENY` rules, cross-tenant tests in every module | A SQL owner/migrator identity bypasses RLS by design; it is not used by the running apps |
| Authorization | **E** missing permission check | Architecture test: every action has a decision; sweep test; maker-checker and SoD in the engine, not the UI | New code that passes the test with a wrong policy needs review |
| Invoices / workflow | **T** change after approval | Material change triggers re-approval (ADR-0016); versioned workflow definitions | Immaterial fields can change by design |
| | **R** "I did not approve that" | Immutable audit, actor and correlation ids, evidence bundle | |
| | **T** duplicate invoice | Duplicate detection on vendor, number, date, amount; ERP reconciliation | Deliberate near-duplicates are flagged for a human |
| Payments | **T/E** change bank details then release | Two-person verification, callback reference, block release while a change is open, independent release (SoD), step-up | Collusion of two authorised people (detected by audit and reports, not prevented) |
| | **I** bank details exposed | Column encryption, masked by default, reveal is permissioned, step-up and audited | Reveal by an authorised person is legitimate use |
| | Money movement | None: v1 prepares instructions only; there is no bank connection | |
| Audit | **T** rewrite history | Hash chain, `DENY UPDATE/DELETE` + trigger, anchors in an immutable external container, verifier compares the two, restore markers | An attacker who controls both the database and the anchor account's immutability (before it is locked) |
| | **D** chain contention | Per-tenant chains | Measured: single-tenant write bursts queue (docs/performance.md) |
| Files | **T/I** malicious upload | Type and size limits, quarantine and ClamAV scan, tenant-scoped paths, attachment download with `nosniff` | Zero-day payloads that the scanner does not know |
| ERP | **S/T** forged ERP calls / SSRF | `IErpAdapter`; only documented interfaces; base URL passes `OutboundGuard`; secrets by vault name; idempotency keys | D365 behaviour is stub-tested only (R6) |
| | **I** ERP credential leak | Only the vault name is stored; never in API output, logs or audit | |
| Notifications | **S/I** webhook/SMTP abuse, SSRF | `OutboundGuard`; two-person approval of channel configuration; mandatory kinds locked | |
| Secrets and keys | **I** key theft | Key Vault (RBAC, purge protection, soft delete), managed identities, no secrets in environment or repository; envelope encryption; data-protection key ring wrapped | Loss of the Key Vault key is loss of the data: backup/recovery in [key-rotation.md](../operations/key-rotation.md) |
| Backups | **I** stolen backup | TDE; columns encrypted with keys that are not in the backup (they are in Key Vault) | |
| | **T** restore to an earlier state to hide events | Anchors are ahead of the restored database; verifier reports "events are missing"; accepting a restore needs an explicit, audited operator step | |
| Supply chain | **T** malicious dependency or build | Locked restore, Dependabot, Trivy, CodeQL, SBOM, SHA/digest pins, OIDC deploy (no stored cloud secret), approval for prod | A compromised upstream release inside the pinned version |
| Operations | **E** operator abuse | Operator actions are migrator commands, run as a job with a managed identity; each writes audit events; deploy requires approval | An Azure subscription owner can still change the platform; use PIM and alerts |
| Availability | **D** flooding | Edge WAF, rate limits (per IP and per user), request limits, upload limiter, scale rules | Layer 3/4 DDoS relies on Azure's platform protection |
| Logging | **I** secrets or personal data in logs | Redaction at the formatter; tests | New log statements need review |
