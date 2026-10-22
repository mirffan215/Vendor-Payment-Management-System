# ADR-0020: Dynamics 365 Finance adapter, mapping, secrets, and what was not verified
Status: Accepted
Date: 2026-10-22

## Context
CLAUDE.md allows only documented Dynamics 365 Finance interfaces and forbids writing to its database. There is no D365 environment in
this project's build or test infrastructure, and entity and field names differ between versions and customisations.

## Decision
- **Interface:** OData v4 data entities over HTTPS, authenticated with Entra ID client credentials (`scope = {environment}/.default`).
  Tokens are cached in memory per adapter instance with a two-minute safety margin. Requests carry the OData version headers; updates
  use `If-Match: *`. Nothing else is used: no SQL, no direct database access, no undocumented endpoints.
- **Journal:** approved invoices are posted to a **vendor invoice journal** (one journal header per invoice, one line per invoice line),
  as agreed with the owner. The journal description carries `VPMS:` plus 32 hex characters of the SHA-256 of the idempotency key (D365
  text fields are short); before creating, the adapter looks for a journal with that description and returns the existing number.
  Attachments are stored with a name derived the same way.
- **Vendors:** VPMS is the onboarding source. A verified vendor is created in D365 (found by a stored search-name reference if a lost
  answer left one behind) and the returned account is stored on the vendor-entity link; later changes patch the same account. A different
  account already on the link is a conflict, never silently replaced.
- **Entity-set names** are the documented defaults but each can be overridden per connection (`ErpConfig`), because they vary. **Field
  names are fixed in the adapter** and listed in `D365FinanceAdapter`; they are the part most likely to need adjusting in a first
  customer spike.
- **Failure classification:** 429 is transient and passes on `Retry-After`; 408/500/502/503/504, network errors and timeouts are
  transient; 401/403 are transient as `auth_failed` (a rotated secret recovers without dead-lettering a hundred messages, and ends in the
  dead letter after the attempt limit if not fixed); other 4xx are rejections carrying D365's own message. A failure on any page of a
  listing fails the whole listing rather than returning part of it. Single quotes in filter values are doubled.
- **Configuration:** the tenant's `Connection` (provider, base URL, Entra directory and application ids, secret *name*, entity sets,
  page size, timeout) and each legal entity's `Mapping` (company / data area, journal, posting profile, payment-terms and tax-code maps,
  financial-dimension mappings with defaults and value maps) are versioned `ErpConfig` rows. Every version is pending until a different
  person approves it (`Erp.Configure` / `Erp.ApproveChange`); superseded versions are kept.
- **Secrets:** only the *name* of a secret is stored, validated against a vault-name pattern. `ISecretReader` resolves it when a message
  is sent; the implementation reads configuration key `Secrets:{name}`, and in production Key Vault is added as a configuration provider
  under that prefix. The secret is held in memory for the call, is never returned by the API, never written to the audit trail, the
  queue, logs or errors, and a test asserts it is absent from stored rows, API output and audit entries.
- **Providers available** are those registered by the host. The mock adapter is registered only in Development and tests, so a production
  tenant cannot select it.
- **Testing:** one contract suite (`ErpAdapterContract`) runs against the mock adapter and against `D365FinanceAdapter` over a stub HTTP
  server that implements the token endpoint, the entity sets used, equality filters, paging, generated keys and D365-style refusals.

## What was NOT verified
- **No test has run against a real Dynamics 365 Finance environment.** The stub is written from the documented OData behaviour. Entity
  names, field names, required fields, number sequences and posting behaviour (including whether a journal posts automatically or needs a
  separate posting step) are assumptions to be confirmed in an integration spike with the first customer's sandbox (risk R6).
- The posted-invoice listing used for status and reconciliation (`VendorInvoiceHeaders`, journal description as the key) is the least
  certain mapping: the field that carries our reference after posting may differ.
- Throughput limits and service protection limits of a real environment are unknown; batch sizes and backoff are settings or constants
  that can be tuned once measured.

## Alternatives considered
- Posting to purchase invoices or free-text invoices instead of a journal: chosen against by the owner for this version.
- Fixed field maps in settings: more flexible but a larger surface to misconfigure; entity names are configurable now, fields can follow.
- Storing the client secret encrypted in the database: rejected; the vault is the system of record for secrets.

## Consequences
- Another ERP is one new `IErpAdapter` plus mapping fields; the queue, inbox and reconciliation do not change.
- Going live with a customer needs the spike above before the first real posting.
