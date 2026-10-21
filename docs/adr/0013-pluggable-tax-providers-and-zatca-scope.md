# ADR-0013: Pluggable country tax providers and ZATCA scope
Status: Accepted
Date: 2026-10-21

## Context
Saudi VAT and ZATCA requirements must be supported without hard-wiring one country into the invoice module, and without
assuming VPMS issues tax invoices. The owner confirmed that VPMS is used for the payment process only.

## Decision
- Country checks implement `ITaxRuleProvider` (defined in the invoice domain) and run inside the `tax.vat` verification rule.
  A provider is chosen by the legal entity's country; `*` is the fallback. Providers live in their own assembly
  (`Vpms.Tax.Sa`), reference only the kernel and the invoice domain (architecture-tested), and are registered in the host:
  removing the registration returns that country to the fallback (tested).
- The Saudi provider validates what a **supplier** sent on a **received** invoice: VAT number format, the number against the
  vendor master, an e-invoice reference given in UUID form, tax category and rate (15% since 1 July 2020, 5% before; zero for
  zero-rated, exempt and out-of-scope; no tax amount on those). Each check is a switch per legal entity, held in the entity's
  versioned rule settings, so switching one off is a loosening and needs approval. Requiring the number or reference is opt-in.
- **Not built, by decision:** e-invoice generation, signing, CSID onboarding, clearance or reporting, QR content validation,
  and anything that issues credit or debit notes or self-bills. [docs/zatca-scope.md](../zatca-scope.md) lists obligations by role.
- The invoice stores the supplier tax number as printed (`SupplierTaxNumber`) and the supplier's reference; the vendor master's
  number is read through the vendor contract for comparison only, never shown or logged.

## Alternatives considered
- Saudi rules in the core rule set: couples every deployment to one country.
- Building the issuing side now: contradicts the confirmed scope and adds large, regulated surface.

## Consequences
- Another country is one assembly plus a registration line.
- If VPMS ever issues tax documents, this decision must be reopened (recorded in the risks document).
- A Saudi tax adviser should confirm the receiving-side reading before go-live (open question Q8).
