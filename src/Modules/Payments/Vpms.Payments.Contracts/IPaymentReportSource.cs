namespace Vpms.Payments.Contracts;

/// <summary>Read-only payment facts for reports.</summary>
public interface IPaymentReportSource
{
    /// <summary>For each invoice that has a request still claiming it (Created, InProposal or Released), the request status.</summary>
    Task<IReadOnlyDictionary<Guid, string>> RequestStatusByInvoiceAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct = default);
}
