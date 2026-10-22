# ADR-0016: Re-approval on material change
Status: Accepted
Date: 2026-10-22

## Context
CLAUDE.md: approved invoice data that changes materially must be approved again. "Materially" differs by customer.

## Decision
- **Baseline.** When approval starts, the invoice stores a snapshot of its material fields (totals, currency, vendor, number,
  dates, PO reference, supplier tax number, line count, dimensions). Every later revision is compared with that baseline, not with
  the previous revision, so small edits cannot add up unnoticed.
- **Settings per legal entity** (`MaterialityPolicy`): an amount allowance (percentage and absolute; a money field is material
  when it moves by more than the larger allowance) and the list of fields that count. The defaults make every change material.
  Changes to the settings are versioned and need a second person, like workflows.
- **Revising** an invoice that is in approval or approved is a separate action with a reason. A material change voids the approval:
  the run is cancelled (kept as history), the approval fields are cleared, the invoice goes back through verification and must be
  sent for approval again. A small change keeps the approval: the run follows the new version and amount and the invoice stays
  (or becomes, for an approved one) approved for the new version.
- **The editor becomes a maker** in both cases and cannot approve it.
- **Ordering and recovery.** The invoice change is saved first, then the run is cancelled or rebound. If that second step fails,
  starting approval again replaces a run for an older version, and a late outcome is ignored because it no longer matches the
  invoice's version and state.
- Phase 5 must treat an invoice as payable only when it is Approved and its approved version is the current version.

## Consequences
- Out of the box the system is strict. Customers loosen it knowingly, with a second approver and an audit trail.
- Fields outside the list can change after approval without re-approval; that is the customer's decision.
