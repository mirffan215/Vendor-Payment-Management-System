# Keys: rotation and recovery

## Key hierarchy (ADR-0004)
Key Vault **key-encryption key** (KEK, RSA 3072, `kek` in the vault) wraps one **data key per tenant** (and a second per tenant for index hashing and signing). Wrapped keys are stored in
`CustomerAdmin` with the KEK version that wrapped them. A second Key Vault key (`dataprotection`) wraps the ASP.NET data-protection key ring in blob storage (cookies, antiforgery, MFA challenges).
Nothing but the managed identities can use these keys (`Key Vault Crypto User`: wrap/unwrap only; no export).

## Rotating the key-encryption key (routine, yearly)
Key Vault rotates it automatically each year (`rotationPolicy` in `keyvault.bicep`; a notification 30 days before expiry). A new **version** is created; wrapped data keys keep pointing at the version that wrapped them, and
**old versions must stay enabled** so those keys can still be unwrapped. Do not disable or delete old versions.
New tenants get keys wrapped by the version configured in `Encryption__KeyVaultKeyId`; the Bicep output `kekKeyId` is versioned, so a deployment after a rotation moves new wraps to the new version.
Re-wrapping existing data keys to the newest KEK version is **not built** (finding FR-6); until it is, old KEK versions are needed indefinitely.

Check after a rotation: run `verify-restore` (it unwraps every tenant's keys) — see restore.md.

## Rotating a tenant's data key
`ITenantKeyRing.RotateDataKeyAsync` creates the next version, makes it active and keeps the old one for decryption; it is tested (`EncryptionTests`). There is **no operator command and no re-encryption job yet** (FR-6): after a
rotation, new writes use the new key and existing data stays under the old one. Use cases and what to do today:
- *Routine hygiene:* not required; keys are per tenant and never leave Key Vault unwrapped.
- *A tenant data key is believed compromised:* treat as an incident (incident-response.md). Today the only complete remedy is a re-export and re-import into a fresh tenant, or building the re-encryption job first. Raise it with the owner.

## Data-protection key ring
Stored at `dataprotection/keys.xml` in the files account, wrapped by the `dataprotection` key. If the blob is lost, all sessions and in-flight MFA challenges become invalid (users sign in again); nothing is permanently lost.
Restore from blob soft delete or version history. If the vault key is lost, the ring cannot be read: delete the blob and let the API create a new ring (everyone signs in again).

## Losing the KEK (disaster)
Without the KEK, every tenant data key is unrecoverable, and so are all encrypted columns. Protection: **purge protection and soft delete are on** (a deleted key can be recovered for 90 days), and access needs the vault RBAC role.
1. Recover a deleted key: `az keyvault key recover --vault-name <vault> --name kek`.
2. Keep a **key backup** in a second region: `az keyvault key backup --vault-name <vault> --name kek --file kek.backup` (encrypted blob, only restorable into a vault in the same Azure geography and subscription). Store it in a second vault's storage with restricted access; do this after each rotation.
3. Restore: `az keyvault key restore --vault-name <new vault> --file kek.backup`.
This procedure has not been rehearsed.

## Secrets (per-tenant integrations)
ERP and notification credentials are Key Vault secrets named `Secrets--<name>`; the tenant configuration stores only the name. Rotate by adding a new secret version; the configuration provider reloads within
`KeyVault:ReloadMinutes` (default 15). Use "Test connection" on the integration afterwards.
