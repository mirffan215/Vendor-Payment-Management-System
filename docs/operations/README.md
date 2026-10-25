# Operations

Runbooks for running VPMS in production. Written in Phase 8 from the code and the Bicep; **none has been executed against Azure** (no subscription was available),
except the restore procedure's checks, which `RestoreDrillTests` runs against a real SQL Server backup and restore. Each runbook ends with how to know it worked.
Rehearse them in the test environment before go-live and record the date and duration in the table below.

| Runbook | Use it when |
|---|---|
| [deployment.md](deployment.md) | Releasing, rolling back, first deployment, locking the audit-anchor immutability policy |
| [restore.md](restore.md) | Restoring the database or files, region failure, anchor mismatch after a restore |
| [key-rotation.md](key-rotation.md) | Rotating or recovering the key-encryption key, data-protection key; limits on data-key rotation |
| [incident-response.md](incident-response.md) | A security event, a broken audit chain, a suspected tenant data leak |
| [offboarding.md](offboarding.md) | A customer leaves: suspend, export, shred |
| [retention.md](retention.md) | Setting retention, legal holds, reading the retention report |
| [monitoring.md](monitoring.md) | What each alert means and what to do |
| [admin-guide.md](admin-guide.md) | Customer administrators: setup, roles, identity provider, ERP, notifications |

## Rehearsal log
| Runbook | Environment | Date | Duration | By | Notes |
|---|---|---|---|---|---|
| Restore (automated drill, local SQL Server) | CI/local | 2026-10-25 | seconds | `RestoreDrillTests` | Detects lost events, accepts, continues, keys unwrap |
| All others | - | not yet rehearsed | | | |

## Targets (to be confirmed with the customer; Q12)
| Target | Design value | Basis |
|---|---|---|
| RPO (database) | minutes | Azure SQL point-in-time restore; backups are continuous in practice. Not measured here. |
| RTO (database) | hours, grows with database size | Restore creates a new server/database and the apps are repointed; measure in test |
| RPO (audit anchors) | the anchor interval (hourly job) | Anchors are outside the database; events after the last anchor can be lost without detection |
| RPO (files) | near zero within a region | Zone/geo redundant storage, versioning and 30-day soft delete |
