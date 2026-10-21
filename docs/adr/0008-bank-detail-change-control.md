# ADR-0008: Bank-detail change control
Status: Accepted
Date: 2026-10-21

## Context
Fraudulent bank-detail changes (vendor impersonation) are the highest-impact attack on a payment process (risk R3).
CLAUDE.md requires two-person verification. A rule enforced only in application code is one bug away from failing.

## Decision
- Bank details are never edited. A change creates a new `BankDetails` row (Pending) and a `BankDetailChange` (the
  request). Details become Verified only when the change is Approved by a person other than the maker.
- Enforced in three layers: the domain (`BankDetailChange.Approve` uses `MakerCheckerGuard`), the application
  (approval needs `Vendor.BankDetails.Approve`; the SoD matrix stops one person holding both edit and approve), and
  the database (`CK_BankDetailChange_DifferentPerson`, a trigger that refuses to verify details whose change is not
  approved, a trigger that makes terminal changes immutable, a filtered unique index allowing one Verified row per
  vendor and currency, and DENY DELETE).
- Replacing an existing account, and every change raised from the vendor portal, needs a recorded callback to a known
  vendor contact before approval. Changes expire after 14 days. An open change blocks payment release (Phase 5 reads
  `IBankDetailsLookup.HasPendingChangeAsync`).
- Account identifiers are encrypted (ADR-0004) with a blind index. API reads return only the masked tail; revealing
  needs a dedicated permission, is POST-only and is audited before the value is returned.
- Shared-account control: proposing an account already used by another vendor of the tenant is refused until staff
  confirm. A vendor-portal request in that situation gets a generic refusal ("cannot be accepted online") and an
  audit event, so a vendor learns nothing about other vendors. The check runs in `VendorContext.BeginCrossVendorCheck()`,
  an existence-only scope that architecture tests restrict to `BankDetailsService`.

## Alternatives considered
- Application-only checks: rejected; direct SQL or a future bug would bypass them.
- Editing in place with history: rejected; it makes "what was in effect when payment X was prepared" ambiguous.
- Callback verification as an external manual process only (open question Q9): rejected; a required reference on the
  decision is cheap and leaves evidence.

## Consequences
- Every bank change costs two people and a phone call; this is intended.
- Migrations contain hand-written triggers; model changes to these tables need care.
- Phase 5 must re-check the Verified row and the absence of open changes at release time.
