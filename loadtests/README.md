# Load tests

k6 scenarios against the real API. Results and caveats: [docs/performance.md](../docs/performance.md).

1. Start the load host (seeds a tenant with invoices, serves the API over HTTPS on a self-signed certificate, and stops when the stop file appears):

   ```
   VPMS_LOAD_SERVE=1 VPMS_LOAD_INVOICES=3000 dotnet test tests/Vpms.ApiTests -c Release --filter "FullyQualifiedName~LoadServerTests"
   ```
   It needs Docker (SQL Server runs in Testcontainers). It writes `/tmp/claude-0/load.ready` (JSON with the entity id) when it is ready;
   `VPMS_LOAD_READY_FILE`, `VPMS_LOAD_STOP_FILE`, `VPMS_LOAD_PORT`, `VPMS_LOAD_VENDORS`, `VPMS_LOAD_SECONDS` change the defaults.
2. Run a profile (`smoke`, `load` or `stress`):

   ```
   k6 run -e PROFILE=load -e ENTITY_ID=<entity from the ready file> loadtests/scenarios.js
   ```
3. Stop the host: create the stop file.

The test host lifts the per-user rate limit so the limiter does not hide what the system does; production keeps it. Do not point these scripts at a shared
environment: they create invoices.
