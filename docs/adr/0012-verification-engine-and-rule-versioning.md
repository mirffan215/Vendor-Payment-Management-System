# ADR-0012: Verification engine and per-entity rule versioning
Status: Accepted
Date: 2026-10-21

## Context
Each legal entity decides how strict invoice checks are. Checks must be explainable afterwards ("what ran, on which version of
the invoice, under which settings"), and weakening a control must not be a one-person decision (CLAUDE.md: maker-checker).

## Decision
- **Rules are code, settings are data.** The engine is a pure function over facts passed in (`VerificationEngine.Run`): no
  database, clock or network inside a rule. A fixed catalogue lists the rules (duplicate exact, same number, similar; PO
  required and match; totals; line amounts; VAT; dates; vendor status), each with a switch, a severity (Block or Flag) and
  bounded parameters. There is no expression language.
- **Versioned settings per legal entity** (`VerificationRuleSet`): a change creates the next version, versions are never edited
  or deleted, and one version is Active (database-enforced). An entity with no version uses the built-in defaults as version 0.
  Every verification run records the invoice version and rule-set version it used, and one result row per rule, including rules
  that were skipped.
- **Loosening needs a second person.** A change that switches a rule off, lowers Block to Flag, or moves a threshold in the
  loosening direction (named per parameter in the catalogue) stays Pending until a different person with the approval
  permission approves it. Tightening takes effect at once. Only one change may be pending per entity. Configuring and
  approving cannot be held by one person (SoD matrix). The vendor-status rule cannot be switched off or lowered.
- **Outcome.** A failed Block or Flag becomes an `InvoiceException`; the invoice is `Verified` only when none is open. A flag
  can be accepted with a reason; a block needs the override permission and a person other than the invoice's creator and last
  editor, and every refusal is audited. A waiver counts only for the invoice version it was given for. Vendor status can never
  be waived. Editing an invoice or re-verifying after the rules or a purchase order changed re-runs the same engine.
- **Duplicates** compare within the same vendor and legal entity (each entity keeps its own invoice data), ignore drafts,
  cancelled and rejected invoices, and use canonical numbers (letters and digits, upper case, no leading zeros). A keyed hash
  of vendor, number, total and date is stored for lookup; fuzzy matching uses edit distance with per-entity thresholds.
- **PO matching** is two-way (invoice against order) using our own purchase-order tables, filled by a staff import until the
  ERP adapter (Phase 6) takes over; cumulative use includes other live invoices and history imported from the ERP.
- Verification runs synchronously after submit and edit; a worker sweep verifies anything left waiting.

## Alternatives considered
- Settings editable in place: loses the record of what applied to an old invoice.
- One global rule set: entities differ in tolerance and risk.
- Letting each tenant script rules: unreviewable and a security risk.

## Consequences
- Adding a rule means a code change, a catalogue entry and tests; settings for it appear automatically with safe defaults.
- Fuzzy thresholds need tuning with real data; defaults flag rather than block.
- Three-way matching needs goods receipts from the ERP and is not part of this phase.
