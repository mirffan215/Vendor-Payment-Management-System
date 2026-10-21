# Vendor Payment Management System (VPMS)

Multi-tenant SaaS that approves vendor invoices and **prepares** payment instructions. It never transfers money and
does not issue invoices. Design documents live in [`docs/`](docs/); delivery follows [`docs/roadmap.md`](docs/roadmap.md).

Current status: **Phase 1 (foundation) complete** — see [`docs/phase-1-summary.md`](docs/phase-1-summary.md).

## Repository layout

| Path | Contents |
|---|---|
| `src/BuildingBlocks` | Shared kernel: tenancy, persistence base, audit port, SoD primitives, tenant-scoped storage/cache/jobs |
| `src/Modules/{Identity,CustomerAdmin,Audit}` | Modules, each with `Domain`, `Application`, `Infrastructure`, `Contracts` |
| `src/Host/Vpms.Api` | ASP.NET Core 10 Web API (BFF cookie sessions, per-tenant OIDC) |
| `src/Host/Vpms.Worker` | Background host (audit chain verification and anchoring) |
| `src/Host/Vpms.Migrator` | Applies EF Core migrations and provisions the restricted application login |
| `web/` | React + TypeScript (Vite), English/Arabic, RTL |
| `tests/` | Unit, architecture, integration (Testcontainers SQL Server), API tests |
| `infra/` | Bicep skeleton |
| `docs/` | Architecture, data model, ADRs, roadmap, risks |

## Prerequisites

.NET 10 SDK, Node 22, Docker (integration/API tests start SQL Server with Testcontainers).

## Run the tests

```bash
dotnet test Vpms.sln                      # unit, architecture, integration, API tests
cd web && npm ci && npm test              # unit/component tests (also checks translations and hard-coded strings)
cd web && npx playwright install chromium && npm run test:e2e   # RTL/layout smoke tests
```

## Run locally

```bash
cp .env.example .env                      # local-only passwords; never commit real secrets
docker compose up --build                 # SQL Server, migrations, API (:8080), worker, web (:5173)
```

Or without containers for the API: start SQL Server, then

```bash
export ConnectionStrings__Migrator='Server=localhost,1433;Database=Vpms;User Id=sa;Password=...;TrustServerCertificate=True'
export VPMS_APP_LOGIN=vpms_app_login VPMS_APP_LOGIN_PASSWORD=...
dotnet run --project src/Host/Vpms.Migrator
export ConnectionStrings__Default='Server=localhost,1433;Database=Vpms;User Id=vpms_app_login;Password=...;TrustServerCertificate=True'
dotnet run --project src/Host/Vpms.Api
cd web && npm run dev                     # proxies /api and /auth to the API (VPMS_API to override)
```

The API must connect as the restricted application login, **not** as the owner: audit immutability and row-level
security rely on the database permissions that login gets (see ADR-0007).

## Conventions

- Every feature ships with tests and updated docs; hard-to-reverse decisions are ADRs in `docs/adr`.
- Secrets never go in the repository. Identity-provider client secrets are referenced by name (`Secrets:<name>`),
  populated from Key Vault in deployed environments.
