namespace Vpms.Payments.Contracts;

/// <summary>What the invoice module must tell the payment module, and ask it, when an invoice changes.</summary>
public interface IPaymentLifecycle
{
    /// <summary>
    /// The invoice was revised, cancelled or rejected. Any open payment request for it is invalidated and so is the proposal that
    /// holds it (its other requests become available again). Does nothing when there is no open request.
    /// </summary>
    Task InvoiceChangedAsync(Guid invoiceId, string reason, CancellationToken ct = default);

    /// <summary>True once a payment instruction has been prepared for the invoice. Such an invoice can no longer be changed.</summary>
    Task<bool> HasReleasedPaymentAsync(Guid invoiceId, CancellationToken ct = default);
}
