# Monitoring and alerts

Signals: API and worker logs (structured JSON, secrets and personal data redacted) and traces/metrics (OpenTelemetry → Application Insights), container logs in Log Analytics (365 days), SQL and Key Vault diagnostics, Front Door WAF logs.
Alerts are defined in `infra/modules/alerts.bicep`; they notify the `<prefix>-oncall` action group (e-mail receivers from `VPMS_ALERT_EMAILS`; add a webhook for chat or paging).

| Alert | Severity | Query / metric | What to do |
|---|---|---|---|
| `audit-chain-broken` | 0 (page) | console logs "AUDIT CHAIN BROKEN" / "Events are missing" | incident-response.md → Audit chain |
| `worker-silent` | 1 (page) | no `vpms.job.runs` for 20 min | Check the worker app (restarts, logs, `startup-refused`); jobs resume where they stopped (leases) |
| `startup-refused` | 1 | "Unsafe Production configuration" | Fix the named setting and redeploy |
| `api-5xx` | 2 | > 5 failed requests with 5xx in 10 min | Open the failed requests in Application Insights (operation id); check dependencies (SQL, Key Vault); roll back if it began with a release |
| `job-failures` | 2 | ≥ 3 failed job passes in 30 min | Query `vpms.job.runs` by `job`; read the exception in the worker log; dead-letters are visible on Notifications and ERP admin screens |
| `security-events` | 2 | > 50 security events in 15 min | Break down by `type` (login failure, access denied, CSRF, rate limited, step-up required) and source address |
| `sql-storage` | 2 | > 85 % of max size | Raise `sqlMaxSizeGb` in the parameter file and redeploy |
| `api-latency` | 3 | p95 > 3 s over 15 min | Compare with docs/performance.md; look at SQL CPU (alert `sql-cpu`) and slow queries in Query Performance Insight |
| `sql-cpu` | 3 | > 85 % for 15 min | Scale the SKU or investigate the query |

## Dashboards to build first (not provided)
Request rate and p95 by route; `vpms.job.runs` by outcome; queue depth/dead letters (gauges); WAF blocks; SQL DTU/CPU. The meters are named in `VpmsTelemetry`.

## Health endpoints
`/health/live` (process), `/health/ready` (database and storage reachable). The worker has a heartbeat file checked by the container `HEALTHCHECK` (`--healthcheck`); Container Apps does not run exec probes, so the `worker-silent` alert is the signal.

## Not covered
No availability test from outside (add an Application Insights standard test on the public address), no cost alerts, no per-tenant dashboards.
