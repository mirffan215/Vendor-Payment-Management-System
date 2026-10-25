# ADR-0025: Deployment topology, blue/green releases and migrations
Status: Accepted
Date: 2026-10-25

## Context
Phase 8 needs a production deployment that the team can repeat, review and roll back, on Azure, without ties that stop a later move (CLAUDE.md:
"Azure-first, portable"). The owner chose Azure Container Apps for hosting and Bicep for infrastructure as code. Constraints that shape it:
- The database holds financial records and a hash-chained audit trail; a release must never leave it half-migrated or reachable by an unprepared version.
- The application identities must not hold passwords, and secrets must not be environment variables or files in the repository.
- Two versions of the API briefly run together (while traffic moves), so the database must work for both.

## Decision
1. **Hosting.** One Container Apps environment per deployment environment (dev, test, prod) in a VNet subnet. Apps: `web` (nginx serving the SPA and proxying
   `/api`, `/auth`, `/portal/auth` to the API; the only app with external ingress), `api` (internal ingress), `worker` (no ingress), `clamav` (internal TCP), and a
   `migrator` job (manual trigger). Images come from a private Premium registry, pulled by managed identity.
2. **Identities, not credentials.** Three user-assigned managed identities for api, worker and migrator plus one for web (registry pull only). SQL is Entra-only
   (no SQL logins); the migrator, running as the SQL administrator group member, creates contained users for the api and worker identities and adds them to `vpms_app`
   (`VPMS_ENTRA_PRINCIPALS`). Key Vault, storage and the registry are reached with role assignments (data-plane RBAC), never keys. Secrets are read with the Key Vault
   configuration provider; the data-protection key ring is in blob storage and wrapped by a Key Vault key (ADR-0023).
3. **Network.** Production runs the database, vault, storage and anchors behind private endpoints (`privateNetworking`), with private DNS zones. The registry stays
   reachable from the pipeline (Entra-only, no anonymous pull). An optional Front Door Premium with WAF fronts `web`; `web` refuses requests without that profile's id.
4. **Audit anchors** live in a separate storage account with a time-based immutability policy (ADR-0003, Phase 8 slice 6). The policy is created *unlocked*;
   locking it is irreversible and is a deliberate manual step in the deployment runbook once the retention period has been agreed.
5. **Releases are blue/green by revision.** `api` and `web` run in Multiple-revision mode. The pipeline (1) deploys the platform, (2) builds or reuses a tagged image,
   (3) runs the migrator job and waits for it, (4) deploys a new revision of api and web at 0% traffic with the previous revision still at 100%, (5) waits until the new
   revisions report healthy, (6) moves 100% of traffic, (7) smoke-tests through the public address, and (8) on any failure puts the traffic back on the previous revision.
   Older revisions are deactivated only after a success, and the previous revision is kept for a fast rollback. The worker is single-revision and updated in place.
6. **Migrations are expand/contract.** A migration that runs before the traffic moves must work for the version still serving: add columns nullable or with defaults,
   add tables and indexes, never drop or rename in the same release that stops using them. Destructive changes ship one release later. The migrator is idempotent and
   retries transient SQL errors. A migration that cannot satisfy this is announced as a maintenance window in the release notes and run with traffic stopped.
7. **Parameters, not copies.** `infra/main.bicep` takes everything environmental as parameters; `main.{dev,test,prod}.bicepparam` hold the differences (private
   networking, zone redundancy, Front Door, deletion locks, replica counts). Release inputs (image tag, revision suffix, traffic weights) arrive as environment
   variables from the pipeline. Pipeline-to-Azure login is OpenID Connect federation; no cloud credential is stored in GitHub.
8. **Approval.** The `prod` GitHub environment requires a named approver; what-if output is an artefact reviewed before approving. Deployments to one environment never overlap.

## Alternatives considered
- **AKS.** More control and portability, much more to operate for a team of this size; Container Apps runs ordinary containers, so a move stays possible.
- **App Service slots.** Mature swap-based blue/green, but the worker and the ClamAV daemon would need different hosting anyway.
- **SQL authentication with passwords in Key Vault.** Simpler to bootstrap, but it leaves a long-lived secret and a login the audit rules cannot tie to a workload.
- **Terraform.** Equally capable; Bicep was chosen by the owner and keeps `infra/` as it was.
- **Rolling update in place.** No fast rollback and no health gate before users see the new version.

## Consequences
- The release path and the topology are verified only by compiling and linting the Bicep and by reading the workflow; **nothing has been deployed** (no Azure
  subscription was available). The first deployment to dev is expected to find mistakes in resource properties and role scopes; the runbook says to do it before test.
- Two versions share the database for a few minutes per release; the expand/contract rule is a discipline, enforced by review, not by the tooling.
- `web` and Front Door depend on each other (the web app needs the Front Door id; the Front Door needs the web address): the first prod deployment is run twice,
  and the id is then stored as an environment variable.
- The container apps' own address stays reachable when Front Door is off (dev), and the WAF check can be bypassed by anyone who knows the Front Door id. The id
  is not a secret; the protection against bypass is the network (private ingress), which a later hardening step can add.
- Costs rise with Premium registry, Front Door Premium and zone redundancy; dev uses none of them.
