# Deployment runbook

Design: [ADR-0025](../adr/0025-deployment-and-blue-green.md). Pipeline: `.github/workflows/deploy.yml`. Infrastructure: `infra/`.

## One-time setup per environment
1. Create the resource group, and an Entra **group** for SQL administrators (`VPMS_SQL_ADMIN_OBJECT_ID`, `VPMS_SQL_ADMIN_NAME`). Add the deployment identity (below) to it: the migrator job's
   identity must also be added after the first platform deployment (its name is the `migratorIdentityName` output), so it can create the database users.
2. Create an Entra app registration for GitHub with a **federated credential** for the repository and the GitHub environment (`dev`, `test`, `prod`). Grant it `Contributor` and
   `User Access Administrator` on the resource group (the Bicep assigns roles). No client secret is created.
3. In GitHub, create environments `dev`, `dev-plan`, `test`, `test-plan`, `prod`, `prod-plan`. `prod` requires reviewers; the `-plan` environments need none (what-if is read-only).
   Set environment variables: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `VPMS_DEPLOYER_OBJECT_ID`, `VPMS_SQL_ADMIN_OBJECT_ID`,
   `VPMS_SQL_ADMIN_NAME`, `VPMS_PUBLIC_HOST` (prod), `VPMS_ALERT_EMAILS` (JSON array), `VPMS_FRONT_DOOR_ID` (after the first run).
4. Register the application with each tenant's identity provider later, per customer (admin guide).

## First deployment (dev, then test, then prod)
1. Run **Deploy** with the environment. The what-if job shows what will be created; read it. Approve (prod).
2. The first run creates the platform, pushes images, creates the apps, runs the migrator (the apps may restart until it finishes) and smoke-tests.
3. With Front Door: copy the `frontDoorId` output into the environment variable `VPMS_FRONT_DOOR_ID` and run Deploy again so the web app only accepts Front Door traffic. Add the custom domain
   in Front Door, validate DNS, and set `VPMS_PUBLIC_HOST` to it (this also changes `AllowedHosts`).
4. Provision the first tenant with the migrator command `provision-tenant` (README). Check the sign-in with the tenant's identity provider.
5. **Checklist before go-live:** health of all apps; alert action group receives a test; Log Analytics shows API requests and worker `vpms.job.runs`; the data-protection key ring blob exists;
   SQL audit logs arrive; the restore drill (restore.md) done on this environment; the anchor immutability policy decision (below).

## Routine release
Run **Deploy** with the environment (empty `image_tag` builds the selected commit; set a tag to promote an image that already ran in a lower environment).
The workflow: what-if → platform → build/push → **migrator job (waits)** → new revisions at 0% → wait healthy → move traffic → smoke test → deactivate old revisions.
- Migrations run *before* traffic moves and must work for the old version too (expand/contract, ADR-0025). Never drop or rename a column in the release that stops using it.
- If anything fails after the new revision exists, the workflow puts traffic back on the previous revision.

## Rollback
- **Immediately:** `az containerapp ingress traffic set -g <rg> -n vpms-<env>-api --revision-weight <previous-revision>=100`, and the same for `-web`. The previous revision stays active until the next successful release.
- **Worker:** updated in place. Run Deploy with the previous `image_tag`.
- **Database:** do not roll the database back for a bad release; ship a fix forward (expand/contract makes the old code work with the new schema). A restore is a different procedure (restore.md).

## Locking the audit-anchor immutability policy (irreversible)
The anchors container is created with a **time-based retention policy that is not locked** (`anchors.bicep`, `immutabilityDays` = 3650 by default), so mistakes in dev can be corrected. Once the
retention period is agreed with the customer and the environment is production:
1. Check the policy: `az storage container immutability-policy show --account-name <anchor account> --container-name audit-anchors`.
2. Lock it: `az storage container immutability-policy lock --account-name <anchor account> --container-name audit-anchors --if-match <etag>`. After this the period can only be lengthened and the container cannot be deleted while it holds anchors.
3. Record the date here and in the change log.
The production start-up guard refuses to run without `Audit:AnchorSink=Blob`, but it cannot see whether the policy is locked: this step is a human check.

## Manual access and break-glass
Nobody has standing data-plane access to SQL, storage or Key Vault except the managed identities. Use Entra PIM for the SQL administrator group and the resource group; every use is in the Azure activity log.
