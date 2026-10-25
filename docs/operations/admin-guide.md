# Administrator guide (customer administrators)

Audience: the people who set VPMS up for their organisation. Screens are under **Administration** unless stated. VPMS prepares payment instructions and never moves money.

## First sign-in
The operator creates your tenant and your first identity provider (Microsoft Entra ID, Google or any OpenID Connect provider) and your administrator account. Sign in with your normal work account. You get the *Tenant Administrator* role on first sign-in.
Sensitive actions ask you to sign in again (step-up); this is normal and required.

## Set up, in this order
1. **Legal entities:** one per company that has its own vendors, approvers, ERP company and currency. Each entity keeps its own accounting rules, approvals and payment records even when the same vendor serves several.
2. **Users and roles:** people appear when they first sign in (or add them). Assign roles per legal entity. Roles are deliberately small so duties stay separate:
   - *Invoice Clerk* enters invoices; *Invoice Approver* approves; *Payment Preparer* prepares; *Payment Approver* approves payments; *Payment Releaser* releases (different person from the approver and the preparer).
   - *Vendor Manager* creates vendors; *Vendor Approver* approves; *Bank Details Approver* is the second person for any bank change.
   - Configuration roles (Workflow, Verification, ERP, Notification, Retention Administrators) propose changes; the matching *…Approver* role approves them. One person cannot hold both sides of the same change.
   - *Auditor* and *Report Viewer* read only. *Tax Reviewer* sees the input-VAT register.
   To remove access: disable the user (immediate) and use **Sign out everywhere** if you suspect misuse.
3. **Workflows:** define approval chains per entity and invoice type (levels, conditions such as amount, sequential or parallel). A new version needs a second person's approval. Existing invoices finish on the version they started with.
4. **Verification rules:** duplicate detection, PO matching tolerances, VAT/amount checks; changes also need approval.
5. **ERP (Dynamics 365 Finance):** enter the environment address, company mapping and the **name** of the secret that holds the credential (the operator stores the secret in the vault; the secret value is never shown). Activation needs a second person. Use *Test connection*.
6. **Notifications:** e-mail (Microsoft Graph or SMTP), Teams webhook, in-app. Channel changes need approval. Mandatory notices cannot be switched off.
7. **Retention:** see retention.md. Place legal holds here.

## Everyday
- **Invoices:** enter or receive by e-mail/document; verification runs automatically; exceptions go to a reviewer; approvals follow the workflow; if approved data changes materially, it goes back for approval.
- **Payments:** approved invoices are grouped into proposals by vendor, due date, company and currency; approved, then released by a different person. Release produces a signed instruction file for your bank process. **VPMS does not send it to a bank.**
- **Bank-detail changes:** one person enters, a different person verifies (with the callback reference). Payment for that vendor is blocked while a change is open.
- **Audit:** *Audit* shows who did what; *Evidence* downloads a signed bundle for a period, and *Verify* checks a bundle later.
- **Language and calendar:** each person chooses English or Arabic (right to left) and Gregorian or Hijri dates in their profile.

## Saudi Arabia
VPMS records the VAT on **received** supplier invoices and shows the input-VAT register. Issuing tax invoices and ZATCA e-invoicing are not part of VPMS (docs/zatca-scope.md); confirm your own obligations with your tax adviser.

## Getting help
Give the operator the correlation id shown in an error message; it finds the request in the logs.
