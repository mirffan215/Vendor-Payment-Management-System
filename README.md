# Vendor Payment Management System (VPMS)

Multi-tenant SaaS that approves vendor invoices and **prepares** payment instructions. It never transfers money and
does not issue invoices. Design documents live in [`docs/`](docs/); delivery follows [`docs/roadmap.md`](docs/roadmap.md).

Current status: **Phase 8 (hardening) complete** — see [`docs/phase-8-summary.md`](docs/phase-8-summary.md); operations runbooks in [`docs/operations/`](docs/operations/README.md); security review in [`docs/security/`](docs/security/asvs-l2.md)
(earlier: [Phase 7](docs/phase-7-summary.md), [Phase 6](docs/phase-6-summary.md), [Phase 5](docs/phase-5-summary.md), [Phase 4](docs/phase-4-summary.md), [Phase 3](docs/phase-3-summary.md), [Phase 2](docs/phase-2-summary.md), [Phase 1](docs/phase-1-summary.md)).

## Repository layout

| Path | Contents |
|---|---|
| `src/BuildingBlocks` | Shared kernel: tenancy, persistence base, audit port, SoD primitives, tenant-scoped storage/cache/jobs |
| `src/Modules/{Identity,CustomerAdmin,Audit,Documents,Vendors,VendorPortal,Invoices,Workflow,Payments,ErpIntegration}` | Modules, each with `Domain`, `Application`, `Infrastructure`, `Contracts` |
| `src/Host/Vpms.Api` | ASP.NET Core 10 Web API (BFF cookie sessions, per-tenant OIDC) |
| `src/Modules/Tax/Vpms.Tax.Sa` | Saudi VAT checks for received invoices (plug-in, see [docs/zatca-scope.md](docs/zatca-scope.md)) |
| `src/Host/Vpms.Worker` | Background host (audit chain verification and anchoring, attachment scanning, bank-change expiry, invoice email intake, extraction and verification sweep, approval timers, payment integrity sweep, ERP outbound queue and inbound pull) |
| `src/Host/Vpms.Migrator` | Applies EF Core migrations, provisions the restricted application login, and provisions tenants (`provision-tenant`) |
| `web/` | React + TypeScript (Vite), English/Arabic, RTL |
| `tests/` | Unit, architecture, integration (Testcontainers SQL Server), API tests |
| `infra/` | Bicep for Azure: SQL (Entra-only), Container Apps, Key Vault, storage, immutable audit anchors, Front Door + WAF, alerts; `main.{dev,test,prod}.bicepparam` |
| `loadtests/` | k6 scenarios against the real API ([docs/performance.md](docs/performance.md)) |
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

## Provisioning a tenant

Tenants are created by an operator, not through a web surface. The migrator creates the tenant, system roles, the
first administrator (as a pending role assignment, activated at first sign-in) and the first identity provider:

```bash
dotnet run --project src/Host/Vpms.Migrator -- provision-tenant \
  --name "Acme Holdings" --slug acme --admin-email admin@acme.com \
  --idp-kind entra|google|oidc --idp-name "Acme SSO" --idp-authority https://login.example/tenant/v2.0 \
  --idp-client-id <client-id> --idp-secret-ref <secret-name> --domains acme.com [--locale en]
```

The administrator then signs in, adds legal entities, roles and further providers, and invites vendor users from a
vendor's page. Invitation links are logged in development; email delivery arrives in Phase 7.

## Configuration

| Setting | Meaning |
|---|---|
| `Storage:Provider` | `InMemory` (default, development only), `AzureBlob` or `S3` (`Storage:Azure:*`, `Storage:S3:*`); endpoints and credentials via configuration / Key Vault |
| `Encryption:Provider` | `Local` (default, development only; `Encryption:LocalKek`) or `KeyVault` (`Encryption:KeyVaultKeyId`); production refuses `Local` |
| `Documents:Scanner` | `ClamAv` (default; `Documents:ClamAv:Host`/`Port`) or `Eicar` (development/tests); files stay in quarantine until scanned |
| `Portal:PublicBaseUrl` | Base URL used in vendor invitation links |
| `Portal:PasswordHashIterations` | PBKDF2 iterations (default 600,000; lowered only in tests) |
| `RateLimiting:PortalAuthPermitLimit` | Requests per window for vendor sign-in endpoints |
| `Graph:TenantId`, `Graph:ClientId`, `Graph:ClientSecret` | Microsoft Graph app registration for reading invoice mailboxes (application permission `Mail.ReadWrite`); secret from Key Vault. Unset = mailbox reading is off |
| `Invoices:MailPollSeconds`, `Invoices:ExtractionSeconds`, `Invoices:VerificationSweepSeconds` | Worker intervals |

In production the web server must route `/api`, `/auth` and `/portal/auth` to the API (the Vite dev server does).

## Invoices in brief

Invoices reach VPMS by hand entry, document upload, the vendor portal or email, always as a draft that a person completes.
OCR output (when an engine is added) is only ever a suggestion. On submit the invoice is verified against its legal entity's
rules (duplicates, purchase-order match, amounts, VAT, dates, vendor status); failures go to the exceptions queue. Each entity's
rules are versioned, and making a check weaker needs a second person to approve. Purchase orders are imported by staff until the
ERP integration supplies them. See ADR-0011 to ADR-0013.

## Reports, notifications and languages (Phase 7)

- Reports (`/reports`): payables aging, payment forecast, exceptions, approval delays and the input-VAT register, exportable as CSV, Excel and PDF. Every export is audited.
- Audit evidence (`/audit`): download a signed bundle for a date range and verify it later.
- Notifications: in-app bell and inbox always; email (Microsoft Graph or SMTP) and Teams per tenant after a second person approves the channel. Secrets are entered as vault names.
- Each person chooses language (English, Arabic) and calendar (Gregorian, Hijri) in the header; the choice is stored on their account.
- VPMS receives supplier invoices; it does not issue tax invoices or talk to ZATCA clearance (`docs/zatca-scope.md`).

## Conventions

- Every feature ships with tests and updated docs; hard-to-reverse decisions are ADRs in `docs/adr`.
- Secrets never go in the repository. Identity-provider client secrets are referenced by name (`Secrets:<name>`),
  populated from Key Vault in deployed environments.
