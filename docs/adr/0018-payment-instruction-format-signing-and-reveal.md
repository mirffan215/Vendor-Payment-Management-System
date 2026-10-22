# ADR-0018: Payment instruction format, signing and bank-detail reveal
Status: Accepted
Date: 2026-10-22

## Context
The released proposal must leave the system as a file or payload for whoever makes the bank transfer. The bank file format is not
decided (open question Q6). The file contains full account numbers, which are encrypted everywhere else.

## Decision
- **Exporter interface.** `IInstructionExporter` turns an `InstructionModel` into files. The first implementation produces a generic
  CSV (one row per invoice paid) and a structured JSON, versioned as "CSV+JSON v1". Bank-specific formats (ISO 20022 pain.001,
  SAMA) are new implementations; approval, release and storage do not change. Spreadsheet formula injection is defused in CSV
  cells (a leading `=`, `+`, `-`, `@` is prefixed) and every cell is quoted.
- **Reveal only at release.** Full IBAN/account numbers are decrypted through `IBankDetailsLookup.RevealForPaymentAsync`, which
  refuses details that are not Verified and writes an audit record before returning. It is called only when a release has passed
  separation and re-checks; a refused release reveals nothing. APIs never return full account numbers.
- **Encrypted at rest.** The CSV and JSON are stored encrypted with the tenant data key (column bound into the ciphertext).
- **Signed.** The instruction's hash is SHA-256 over both files; the signature is a keyed hash (HMAC-SHA-256 with the tenant's index key
  and a fixed purpose label) of that hash. Download and an explicit verify endpoint recompute both; a mismatch refuses the download
  and writes `payment.instruction.tampered`. The signature proves the file is what the system prepared, not who authorised it: that
  is the `ReleaseAuthorization` (releaser, time, separation and re-check results) referenced from the instruction.
- **Download is a controlled event.** It needs `Payment.Instruction.Download` for the entity, is audited each time, sends
  `Cache-Control: no-store`, and the first download records the hand-off.

## Alternatives considered
- Asymmetric signature with a customer-held key: stronger non-repudiation, but needs key onboarding per tenant; revisit with the bank
  integration.
- Storing files in blob storage: adds scanning and path concerns for content that is small and sensitive; the database column keeps
  it inside the tenant isolation and encryption model.

## Consequences
- The instruction is only as private as the people allowed to download it; the permission is separate from release.
- Changing the exporter version changes the format string recorded on each instruction.
