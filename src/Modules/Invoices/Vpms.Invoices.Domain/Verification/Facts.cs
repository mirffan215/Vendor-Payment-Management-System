namespace Vpms.Invoices.Domain.Verification;

public sealed record LineFacts(
    int LineNo, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount, string? TaxCode, decimal TaxRate, decimal TaxAmount, string? PoLineRef);

public sealed record InvoiceFacts(
    Guid Id, Guid VendorId, Guid LegalEntityId, string InvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, string CurrencyCode,
    decimal Subtotal, decimal TaxTotal, decimal Total, string? PoReference, string? SupplierTaxInvoiceRef, IReadOnlyList<LineFacts> Lines,
    string? SupplierTaxNumber = null)
{
    public string CanonicalNumber => InvoiceNumber.Length == 0 ? "" : Domain.InvoiceNumber.Canonical(InvoiceNumber);
}

/// <summary>Another live invoice of the same vendor in the same legal entity, as the duplicate rules see it.</summary>
public sealed record CandidateFacts(Guid InvoiceId, string InvoiceNumber, DateOnly InvoiceDate, decimal Total, string CurrencyCode, string Status)
{
    public string CanonicalNumber => Domain.InvoiceNumber.Canonical(InvoiceNumber);
}

public sealed record VendorFacts(bool CanTransact);

public sealed record PoLineFacts(string LineNo, decimal OrderedQuantity, decimal UnitPrice, decimal PreviouslyInvoicedQuantity);

public sealed record PoFacts(string PoNumber, Guid VendorId, string CurrencyCode, string Status, IReadOnlyList<PoLineFacts> Lines, decimal PreviouslyInvoicedValue)
{
    public decimal OrderedValue => Lines.Sum(l => l.OrderedQuantity * l.UnitPrice);
}

/// <summary>Everything a rule may look at. Rules never reach into a database or the clock: all of it is passed in.</summary>
public sealed record VerificationInput(
    InvoiceFacts Invoice, VendorFacts Vendor, IReadOnlyList<CandidateFacts> Candidates, PoFacts? PurchaseOrder, DateOnly Today,
    Tax.ITaxRuleProvider? TaxProvider = null, string? VendorTaxNumber = null);

/// <param name="Key">A stable machine-readable reason, translated by the user interface.</param>
public sealed record Finding(string Key, IReadOnlyDictionary<string, string> Evidence)
{
    public static Finding Of(string key, params (string Name, string Value)[] evidence) => new(key, evidence.ToDictionary(e => e.Name, e => e.Value));
}

public static class Outcome
{
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

public sealed record RuleResult(string RuleCode, string Outcome, string Severity, IReadOnlyList<Finding> Findings)
{
    public bool Failed => Outcome == Domain.Verification.Outcome.Failed;
}

public interface IVerificationRule
{
    string Code { get; }

    /// <summary>Findings for an invoice that breaks the rule; empty when it passes.</summary>
    IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting);
}
