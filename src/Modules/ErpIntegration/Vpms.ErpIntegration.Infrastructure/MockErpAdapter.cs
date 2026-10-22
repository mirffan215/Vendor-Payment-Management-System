using Vpms.ErpIntegration.Contracts;

namespace Vpms.ErpIntegration.Infrastructure;

/// <summary>
/// A deterministic in-memory ERP for tests and local development. It behaves like a well-mannered ERP (a repeated document key is
/// recognised, not duplicated) and can be scripted to fail, throttle, or be tampered with, so queue and reconciliation behaviour can
/// be proven without a real system. Every call is recorded.
/// </summary>
public sealed class MockErpAdapter : IErpAdapter
{
    public const string ProviderName = "Mock";
    private readonly object _gate = new();
    private readonly Dictionary<string, ErpInvoiceStatus> _invoices = new();       // by document key (or reference when no key)
    private readonly Dictionary<string, ErpVendorRecord> _vendors = new();         // by account
    private readonly Dictionary<string, string> _vendorKeys = new();               // idempotency key -> account
    private readonly Dictionary<string, string> _attachmentKeys = new();           // idempotency key -> invoice reference
    private readonly List<ErpPurchaseOrder> _orders = [];
    private readonly List<ErpPayment> _payments = [];
    private readonly Dictionary<string, Queue<ErpResult>> _scripted = new();
    private int _sequence;

    public sealed record Call(string Operation, string? Key);

    public List<Call> Calls { get; } = [];

    public string Provider => ProviderName;

    public string DocumentReference(string idempotencyKey) => idempotencyKey;

    // --- scripting -----------------------------------------------------------------------------------------

    /// <summary>The next calls to the operation return these results instead of working (then it behaves normally again).</summary>
    public void Script(string operation, params ErpResult[] results)
    {
        lock (_gate) _scripted[operation] = new Queue<ErpResult>(results);
    }

    public void FailTimes(string operation, int times, ErpResult failure) => Script(operation, Enumerable.Repeat(failure, times).ToArray());

    public int CountCalls(string operation) { lock (_gate) return Calls.Count(c => c.Operation == operation); }

    public IReadOnlyList<ErpInvoiceStatus> Invoices { get { lock (_gate) return _invoices.Values.ToList(); } }

    public IReadOnlyList<ErpVendorRecord> Vendors { get { lock (_gate) return _vendors.Values.ToList(); } }

    public IReadOnlyList<ErpPayment> Payments { get { lock (_gate) return _payments.ToList(); } }

    public void AddVendor(string account, string name, string? taxNumber = null, string currency = "SAR", bool blocked = false)
    {
        lock (_gate) _vendors[account] = new ErpVendorRecord(account, name, taxNumber, currency, blocked);
    }

    public void AddPurchaseOrder(ErpPurchaseOrder order)
    {
        lock (_gate)
        {
            _orders.RemoveAll(o => o.PoNumber == order.PoNumber);
            _orders.Add(order);
        }
    }

    /// <summary>A document the ERP has that VPMS never posted (entered directly in the ERP).</summary>
    public string AddUnknownInvoice(string vendorAccount, string number, decimal total, string currency = "SAR", string state = ErpDocumentState.Posted)
    {
        lock (_gate)
        {
            var reference = $"VI-{++_sequence:D6}";
            _invoices[reference] = new ErpInvoiceStatus(reference, null, vendorAccount, number, total, currency, DateOnly.FromDateTime(DateTime.UtcNow), state, null);
            return reference;
        }
    }

    public void ChangeInvoice(string documentKey, decimal? total = null, string? vendorAccount = null, string? state = null, DateTimeOffset? paidAt = null)
    {
        lock (_gate)
        {
            var i = _invoices[documentKey];
            _invoices[documentKey] = i with { Total = total ?? i.Total, VendorAccount = vendorAccount ?? i.VendorAccount, State = state ?? i.State, PaidAt = paidAt ?? i.PaidAt };
        }
    }

    public void RemoveInvoice(string documentKey)
    {
        lock (_gate) _invoices.Remove(documentKey);
    }

    public void AddPayment(ErpPayment payment)
    {
        lock (_gate) _payments.Add(payment);
    }

    private ErpResult? Scripted(string operation, string? key)
    {
        Calls.Add(new Call(operation, key));
        return _scripted.TryGetValue(operation, out var q) && q.Count > 0 ? q.Dequeue() : null;
    }

    // --- IErpAdapter ---------------------------------------------------------------------------------------

    public Task<ErpResult> ValidateConnectionAsync(ErpContext context, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Scripted("ValidateConnection", null) ?? ErpResult.Ok("connected"));
    }

    public Task<ErpResult> UpsertVendorAsync(ErpContext context, ErpVendor vendor, string idempotencyKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Scripted("UpsertVendor", idempotencyKey) is { } scripted) return Task.FromResult(scripted);
            if (_vendorKeys.TryGetValue(idempotencyKey, out var existing)) return Task.FromResult(ErpResult.Existing(existing));
            var account = vendor.ExistingAccount ?? $"V-{++_sequence:D5}";
            var isNew = !_vendors.ContainsKey(account);
            _vendors[account] = new ErpVendorRecord(account, vendor.Name, vendor.TaxNumber, vendor.CurrencyCode, false);
            _vendorKeys[idempotencyKey] = account;
            return Task.FromResult(isNew ? ErpResult.Ok(account) : ErpResult.Existing(account));
        }
    }

    public Task<ErpQuery<ErpVendorRecord>> ListVendorsAsync(ErpContext context, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Failure<ErpVendorRecord>("ListVendors") ?? ErpQuery<ErpVendorRecord>.Of(_vendors.Values.OrderBy(v => v.Account).ToList()));
    }

    public Task<ErpResult> CreateVendorInvoiceAsync(ErpContext context, ErpInvoice invoice, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Scripted("CreateVendorInvoice", invoice.DocumentKey) is { } scripted) return Task.FromResult(scripted);
            if (_invoices.TryGetValue(invoice.DocumentKey, out var existing)) return Task.FromResult(ErpResult.Existing(existing.Reference));
            if (!_vendors.ContainsKey(invoice.VendorAccount)) return Task.FromResult(ErpResult.Rejected("vendor_unknown", $"Vendor {invoice.VendorAccount} does not exist."));
            var reference = $"VI-{++_sequence:D6}";
            _invoices[invoice.DocumentKey] = new ErpInvoiceStatus(reference, invoice.DocumentKey, invoice.VendorAccount, invoice.InvoiceNumber, invoice.Total, invoice.CurrencyCode,
                invoice.InvoiceDate, ErpDocumentState.Posted, null);
            return Task.FromResult(ErpResult.Ok(reference));
        }
    }

    public Task<ErpResult> AttachDocumentAsync(ErpContext context, string invoiceReference, ErpAttachment attachment, string idempotencyKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Scripted("AttachDocument", idempotencyKey) is { } scripted) return Task.FromResult(scripted);
            if (_attachmentKeys.ContainsKey(idempotencyKey)) return Task.FromResult(ErpResult.Existing(invoiceReference));
            if (!_invoices.Values.Any(i => i.Reference == invoiceReference)) return Task.FromResult(ErpResult.Rejected("invoice_unknown"));
            _attachmentKeys[idempotencyKey] = invoiceReference;
            return Task.FromResult(ErpResult.Ok(invoiceReference));
        }
    }

    public Task<ErpQuery<ErpInvoiceStatus>> ListInvoicesAsync(ErpContext context, DateTimeOffset? changedSince, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Failure<ErpInvoiceStatus>("ListInvoices") ?? ErpQuery<ErpInvoiceStatus>.Of(_invoices.Values.OrderBy(i => i.Reference).ToList()));
    }

    public Task<ErpQuery<ErpPayment>> GetVendorPaymentsAsync(ErpContext context, DateTimeOffset since, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Failure<ErpPayment>("GetVendorPayments") ?? ErpQuery<ErpPayment>.Of(_payments.Where(p => p.PaidAt >= since).ToList()));
    }

    public Task<ErpQuery<ErpPurchaseOrder>> ListPurchaseOrdersAsync(ErpContext context, DateTimeOffset? changedSince, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Failure<ErpPurchaseOrder>("ListPurchaseOrders") ?? ErpQuery<ErpPurchaseOrder>.Of(_orders.ToList()));
    }

    public Task<ErpQuery<ErpDimensionValue>> GetDimensionValuesAsync(ErpContext context, string dimension, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Failure<ErpDimensionValue>("GetDimensionValues") is { } failed) return Task.FromResult(failed);
            return Task.FromResult(ErpQuery<ErpDimensionValue>.Of([new ErpDimensionValue(dimension, "100", "Default"), new ErpDimensionValue(dimension, "200", "Other")]));
        }
    }

    private ErpQuery<T>? Failure<T>(string operation) =>
        Scripted(operation, null) is { } r ? ErpQuery<T>.Failed(r.Outcome, r.ErrorCode ?? "scripted", r.RetryAfter) : null;
}
