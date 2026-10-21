# ZATCA and VAT: what VPMS does and does not do

Status: Phase 3 position. Confirmed by the product owner (Phase 0): **VPMS does not issue invoices. It is used for the
payment process only.** A Saudi tax adviser should confirm this reading before go-live (open question Q8).

## Which obligations depend on VPMS's role

| Role VPMS could play | ZATCA / VAT implication | In VPMS |
|---|---|---|
| Issuing tax invoices, simplified invoices, credit or debit notes | E-invoice generation (XML), cryptographic stamp, CSID onboarding, clearance or reporting through Fatoora | **Not built.** Nothing in the code generates, signs, clears or reports an invoice |
| Self-billing for suppliers | Issuance duties as above, plus a self-billing agreement | **Not built** |
| Receiving supplier invoices | No issuance duty. The customer needs valid supplier tax invoices to claim input VAT | **Built (Phase 3)** as validation and data capture, see below |
| Archiving invoices and records | Retention for the legally required period in a retrievable form | Per-tenant retention policy in Phase 8; files are kept in write-once storage |
| Paying invoices | VAT amounts and any withholding in the payment instruction must be correct | Amounts are verified before approval (Phases 3, 5) |
| The customer's VAT return and ZATCA reporting | Done by the customer's ERP or tax tooling | **Not built.** VPMS can export data in Phase 7 |

Re-open the decision if VPMS ever issues credit notes, self-billed invoices or any customer-facing tax document.

## What the Saudi module checks on a received invoice

Implemented in `Vpms.Tax.Sa` and run as part of the `tax.vat` verification rule for legal entities whose country is `SA`.
Every check is a switch per legal entity; switching a check off is a loosening and needs a second person's approval
(see ADR-0012).

| Check (switch) | What it does | Default |
|---|---|---|
| `checkSupplierTaxNumberFormat` | The supplier VAT number printed on the invoice is 15 digits and starts and ends with 3 | On |
| `requireSupplierTaxNumber` | The invoice must state the supplier's VAT number | Off |
| `matchVendorTaxNumber` | The number on the invoice equals the tax number on the vendor master | On |
| `checkSupplierTaxInvoiceRefFormat` | A supplier e-invoice reference (the UUID printed on the invoice), when given, is a valid UUID | On |
| `requireSupplierTaxInvoiceRef` | The invoice must carry the supplier's e-invoice reference | Off |
| `checkAllowedRates` | Each line has a known category (S standard, Z zero-rated, E exempt, O outside scope) with the right rate: 15% for S from 1 July 2020 (5% before), 0% for the others, and no tax amount on Z, E or O lines | On |

The generic VAT arithmetic (line tax equals amount times rate, within tolerance) applies to every country. Legal entities in
other countries use the standard fallback provider, which can compare the number with the vendor master and require a
number or reference. A new country is a new assembly implementing `ITaxRuleProvider`, registered in the host; removing the
registration switches that country back to the fallback (tested).

## What the module deliberately does not do

- It does not validate the supplier's e-invoice XML, QR code contents, cryptographic stamp or clearance status. A supplier
  reference is checked only for shape.
- It does not decide whether an invoice is a valid tax invoice in law; it flags missing or inconsistent data for a person.
- Defaults are conservative: required-ness is opt-in per entity because suppliers differ in what they print. Saudi entities
  should review `requireSupplierTaxNumber` and `requireSupplierTaxInvoiceRef` during onboarding.

## Dates

Invoice and due dates are stored as Gregorian dates. Hijri is a display option only (Phase 1), so no VAT rule depends on it.
