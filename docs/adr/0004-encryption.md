# ADR-0004: Encryption and key management
Status: Proposed
Date: 2026-10-21

## Context
Financial data must be encrypted at rest, with column-level encryption for sensitive fields (bank account/IBAN,
tax IDs). Secrets must live in Key Vault. The platform is Azure-first but portable.

## Decision
- Baseline: TDE on the database, encrypted storage accounts/buckets, TLS in transit.
- Column-level: application-level envelope encryption (AES-256-GCM) via an `IFieldEncryptor` abstraction.
  - Key hierarchy: Key Vault key-encryption key (KEK) per environment -> per-tenant data-encryption key (DEK),
    stored wrapped; DEK cached in memory with short TTL.
  - Ciphertext format carries key id and version to support rotation and re-encryption jobs.
- Searchable fields (e.g. IBAN, tax ID for duplicate checks) also store an HMAC-SHA-256 blind index using a
  separate per-tenant key.
- Key Vault access via managed identity; no secrets in the repo; secret scanning in CI.
- The key provider is an interface, so other KMS/HSM backends can be used for portability.

## Alternatives considered
- SQL Always Encrypted (with enclaves): strong separation from DBAs, but ties to SQL Server features, limits EF
  querying, and complicates portability and tests. Reconsider per customer demand.
- TDE only: does not meet the column-level requirement.

## Consequences
- Portable and testable; encryption logic lives in application code and must be reviewed closely.
- Per-tenant DEKs allow tenant offboarding by key destruction (crypto-shredding) and per-tenant rotation.
- Encrypted columns cannot be filtered or sorted in SQL except via blind indexes.
- Rotation requires a background re-encryption job and monitoring.
