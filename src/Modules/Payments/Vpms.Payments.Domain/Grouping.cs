namespace Vpms.Payments.Domain;

/// <summary>Requests that can be paid together: same legal entity, vendor, currency and due date.</summary>
public readonly record struct GroupingKey(Guid LegalEntityId, Guid VendorId, string CurrencyCode, DateOnly DueDate);

public static class ProposalGrouping
{
    public static GroupingKey KeyOf(PaymentRequest r) => new(r.LegalEntityId, r.VendorId, r.CurrencyCode, r.DueDate);

    /// <summary>Groups open requests that are not in a proposal yet, in a stable order (due date, then vendor, then currency).</summary>
    public static IReadOnlyList<(GroupingKey Key, IReadOnlyList<PaymentRequest> Requests)> Group(IEnumerable<PaymentRequest> requests) =>
        requests.Where(r => r.Status == RequestStatus.Created)
            .GroupBy(KeyOf)
            .OrderBy(g => g.Key.DueDate).ThenBy(g => g.Key.VendorId).ThenBy(g => g.Key.CurrencyCode, StringComparer.Ordinal)
            .Select(g => (g.Key, (IReadOnlyList<PaymentRequest>)g.OrderBy(r => r.InvoiceNumber, StringComparer.Ordinal).ThenBy(r => r.Id).ToList()))
            .ToList();
}

public static class RecheckSeverity
{
    /// <summary>The basis of the payment changed for good: the request, and the proposal holding it, are invalidated.</summary>
    public const string Invalidates = "Invalidates";

    /// <summary>Something that may resolve (a bank change awaiting a decision) stops the step for now without invalidating anything.</summary>
    public const string Blocks = "Blocks";
}

public sealed record RecheckFinding(Guid PaymentRequestId, Guid InvoiceId, string Code, string Severity);

public sealed record RecheckResult(IReadOnlyList<RecheckFinding> Findings)
{
    public bool Passed => Findings.Count == 0;

    public bool Invalidates => Findings.Any(f => f.Severity == RecheckSeverity.Invalidates);
}
