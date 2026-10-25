# Restore and disaster recovery

What survives and where: the database (Azure SQL; point-in-time 35 days, weekly/monthly/yearly long-term copies, geo-redundant backup storage), files (storage account with
versioning and 30-day soft delete, GZRS), keys (Key Vault with soft delete and purge protection), and the **audit anchors**, which live in a separate account with an immutability policy.
Because the anchors are outside the database, a restore to an earlier point is *detected*: the audit verifier reports "Events are missing…" until a person accepts it.

## 1. Decide what happened
| Situation | Go to |
|---|---|
| Data deleted or corrupted in the database | 2 (point-in-time restore) |
| A file or document lost | 3 |
| Region unavailable | 4 |
| Verification after any restore | 5 |
| Anchor mismatch without a restore | [incident-response.md](incident-response.md), "audit chain" |

## 2. Restore the database to a point in time
1. Stop writers: scale `worker` to 0 (`az containerapp update ... --min-replicas 0 --max-replicas 0`) and set the `api` traffic to a maintenance revision if you have one; otherwise accept errors during the window.
2. Restore: `az sql db restore --dest-name Vpms-restored --name Vpms --server <server> -g <rg> --time <UTC time>`. Choose the time **before** the damage.
3. Point the apps at the restored database: swap the names (rename `Vpms` to `Vpms-old`, `Vpms-restored` to `Vpms`) or change the connection string and redeploy. Database users and the `vpms_app` role are part of the restore, so the
   managed identities keep access; if you restored to a **different server**, run the migrator once to recreate them (`VPMS_ENTRA_PRINCIPALS`).
4. Run the checks (section 5). Expect `AuditChain` findings "Events are missing…" if the restore point is earlier than the last anchor: that is the system working.
5. If the findings are only that, and the customer agrees to continue from the restored state, **accept the restore**: migrator command
   `accept-restore --slug <tenant> --reason "<ticket and why>"`. This verifies the chain inside the database, records who accepted it and writes a restore marker to the anchors, retiring the anchors
   that are ahead of the database. It is audited and cannot be undone. Do it per tenant.
6. Start the worker. Re-run the checks. Tell customers which events (between the restore point and the incident) are gone; the anchors tell you how many.
7. Keep `Vpms-old` until the investigation is finished, then delete it.

## 3. Restore files
Blob versioning and soft delete: restore a deleted blob (`az storage blob undelete`) or a previous version (copy the old version over the current). Files are under `<tenant id>/…` paths; the database holds the reference and a hash.
The `verify-restore` check lists database rows whose file is missing.

## 4. Region failure
Geo-redundant backup storage lets you restore the database into the paired region (`az sql db restore --geo-backup`), and GZRS storage can fail over (`az storage account failover`). The environment, apps, Key Vault and the registry
are redeployed there with the same Bicep and a different `location`; Key Vault keys must exist in the new vault (a **vault restore** from backup or a vault created with the same key, see key-rotation.md; without the key-encryption key, tenant data keys cannot be unwrapped).
Plan this as a half-day exercise; it has not been run. Data residency (Q10) may restrict the target region.

## 5. Verify
Run the migrator in verify mode (read-only; same settings as the apps):

```
dotnet Vpms.Migrator.dll verify-restore        # job: az containerapp job start with args "verify-restore"
```
It checks, for every tenant: the audit chain is intact; the chain agrees with the anchors (no missing events, no tampering); every stored file the database points to exists; the tenant keys unwrap with the key-encryption key.
Exit code 0 = no findings. Each finding is one JSON line with the tenant, the check and the detail. Then: sign in as a tenant administrator, open an invoice with a bank detail reveal (step-up), run a report.

## Timing and limits
- Azure restore time depends on database size; measure it in the test environment and write it in the rehearsal log (README).
- Between the last anchor and an incident, lost events cannot be detected. The anchor job runs hourly (`Audit:VerifyIntervalMinutes`); shorten it if the customer needs a tighter bound.
- Restoring does not restore the Key Vault. Keep Key Vault purge protection on; do not rely on restoring a purged key.
