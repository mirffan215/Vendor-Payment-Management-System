# Phase 2 summary — Customer administration and vendors

Status: complete, awaiting approval. Branch `claude/bold-wozniak-mkomqb`. Phase 3 has not been started.

## What was built

| Area | Delivered |
|---|---|
| Field encryption (ADR-0004) | AES-256-GCM per field with tenant and column as associated data, per-tenant wrapped data keys, HMAC blind indexes for lookup, key rotation, local (development) and Key Vault key-encryption keys; production refuses the local key |
| Storage and documents (ADR-0010) | Azure Blob and S3-compatible backends behind the tenant-scoped store; `Documents` module with validation, quarantine, EICAR and ClamAV scanners, scan job in the Worker, download only when Clean, audited |
| Customer administration | Tenant profile and branding, legal entities (encrypted tax number), roles and assignments with SoD enforcement and pending assignments, users, identity-provider administration, approval authorities and matrix rules (data only), tenant provisioning command |
| Vendors | Registration and onboarding state machine with maker-checker, duplicate warning by blind index, documents with review, per-legal-entity accounting rules, staff-to-vendor requests |
| Bank details (ADR-0008) | Change requests that take effect only when approved by a different person, enforced in domain, application and database; callback reference for replacements and portal requests; masked reads, audited reveal; expiry job; shared-account fraud control |
| Vendor portal (ADR-0009) | Invite, password, mandatory TOTP and recovery codes; lockout, uniform failures, rate limits; separate session type; portal API for profile, documents, entity links, bank-change requests and message replies |
| Vendor isolation | `IVendorOwned` query filter and write guard on top of tenancy |
| Frontend | Staff: vendors (list, create, lifecycle, documents, bank proposals, invites, requests), bank-change approval queue, administration (entities, users and roles, providers, authorities). Portal: invitation, sign-in with MFA, profile, documents, bank requests, messages. English/Arabic, RTL, Hijri/Gregorian |
| Delivery | ADR-0008/0009/0010, updated architecture, data model, roadmap and README, CI runs the storage emulator tests |

## Acceptance criteria

| Criterion | Evidence |
|---|---|
| A bank-detail change cannot take effect without a second, different approver | Domain tests (`MakerCheckerGuard`); `BankDetailsTests` (service level, including SoD audit event and concurrent approvals); `BankDetailsApiTests` (over HTTP); raw SQL attempts to verify details or approve as the maker are refused by the CHECK constraint and triggers; role assignment refuses edit plus approve for one person; portal-raised changes still need staff approval with a callback |
| Vendor users only see their own data | `VendorIsolationTests` (EF level, every vendor-owned table, write guard, session vendor cannot be swapped) and `VendorPortalDataApiTests` (two vendors in one tenant plus one in another: lists, direct ids = 404, documents, bank changes, threads; vendor cookies rejected by staff endpoints and the reverse; vendor ids in query or headers ignored) |
| Tests cover all of it | Below |

Test counts (all passing): unit 174, architecture 7, integration 153 (5 MinIO tests skipped locally, run in CI),
API 73, web 33 (Vitest) + 7 (Playwright). `dotnet build -c Release` has 0 warnings; no vulnerable NuGet or npm packages.

## Decisions that differ from or extend the plan

- Vendor-level isolation is an EF filter plus write guard, not SQL Server row-level security (ADR-0009).
- A cross-vendor existence scope (`BeginCrossVendorCheck`) exists for the shared-account fraud check only, restricted by
  an architecture test. Found while building slice 7: the vendor filter would otherwise have blinded that control.
- The tenant query filter became a named filter (EF Core 10 cannot combine named and unnamed filters).
- A vendor-portal request for an account used by another vendor is refused with a generic message, not the staff
  confirmation flow, so nothing leaks about other vendors.
- The staff session does not return permissions, so the UI shows every navigation link and relies on server 403s.

## Known limitations and follow-ups

- **Data-protection keys** (used for the vendor sign-in challenge and antiforgery) persist to the local file system;
  production needs a shared, Key Vault-protected key ring (Phase 8).
- **Invitation delivery** is a pluggable interface that logs the link; real email comes with Phase 7.
- **Docker images** are still unverified in this sandbox (TLS proxy), and the web image has no reverse-proxy
  configuration for `/api`, `/auth`, `/portal/auth` (Phase 8).
- **MinIO tests** are skipped when the image cannot be pulled. MinIO no longer publishes on Docker Hub, and the quay.io image was refused on the CI runner, so they were skipped locally and in CI. Set `VPMS_MINIO_IMAGE` to another S3-compatible image to run them. The S3 adapter is still covered by the in-process S3 fake, and Azure by Azurite.
- **Identity-provider scheme eviction** after a provider change is per instance; multi-instance eviction is Phase 8.
- **Audit anchoring** is still a file stand-in (Phase 8).
- No vendor-level RLS predicate and no per-user permission-aware navigation yet.
- Self-service password reset and MFA reset are staff-mediated by design in v1.
- Test-only: a frozen fake clock makes message order ambiguous within the same instant; one test advances the clock.
- CI on GitHub was red on every Phase 2 push before this fix (MinIO image unavailable, then one ordering flake in a test). It is green on the final commit (run 20, all four jobs).

## Open items for you

1. Approve Phase 2, or list changes.
2. Confirm the vendor-isolation approach (EF filter and write guard, no RLS predicate) in ADR-0009.
3. Confirm that portal bank-detail requests which clash with another vendor's account are refused outright.
4. Say "start Phase 3" when ready; I will show a plan first.
