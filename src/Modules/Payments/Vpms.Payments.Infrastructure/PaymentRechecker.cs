using Vpms.Invoices.Contracts;
using Vpms.Payments.Domain;
using Vpms.Vendors.Contracts;

namespace Vpms.Payments.Infrastructure;

/// <summary>
/// The checks that must hold again whenever money gets closer to leaving: when a proposal is approved, and when it is released. Each
/// request is compared with what it froze when it was made: the invoice (identity, approval, integrity hash) and the bank details
/// (still the verified ones, same account, no change waiting).
/// </summary>
public sealed class PaymentRechecker(IPayableInvoiceLookup invoices, IVendorLookup vendors, IBankDetailsLookup bank)
{
    public async Task<RecheckResult> CheckAsync(IEnumerable<PaymentRequest> requests, CancellationToken ct)
    {
        var findings = new List<RecheckFinding>();
        foreach (var r in requests)
        {
            void Add(string code, string severity) => findings.Add(new RecheckFinding(r.Id, r.InvoiceId, code, severity));

            if (r.Status != RequestStatus.InProposal)
            {
                Add("request_not_in_proposal", RecheckSeverity.Invalidates);
                continue;
            }

            var invoice = await invoices.GetAsync(r.InvoiceId, ct);
            if (invoice is null) Add("invoice_missing", RecheckSeverity.Invalidates);
            else if (!invoice.IsPayable) Add("invoice_not_approved", RecheckSeverity.Invalidates);
            else if (invoice.IntegrityHash != r.InvoiceHash) Add("invoice_changed", RecheckSeverity.Invalidates);

            if (!await vendors.CanTransactAsync(r.VendorId, r.LegalEntityId, ct)) Add("vendor_cannot_transact", RecheckSeverity.Invalidates);

            var details = await bank.GetVerifiedAsync(r.VendorId, r.CurrencyCode, ct);
            if (details is null) Add("bank_details_not_verified", RecheckSeverity.Invalidates);
            else if (details.Id != r.BankDetailsId || details.Fingerprint != r.BankFingerprint) Add("bank_details_changed", RecheckSeverity.Invalidates);

            if (await bank.HasPendingChangeAsync(r.VendorId, ct)) Add("bank_change_pending", RecheckSeverity.Blocks);
        }

        return new RecheckResult(findings);
    }
}
