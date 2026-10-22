using System.Globalization;

namespace Vpms.Workflow.Domain;

/// <summary>What routing may depend on. Taken from the subject (an invoice) when the workflow starts.</summary>
public sealed record SubjectFacts(
    decimal Amount,
    string Currency,
    Guid? VendorId,
    string Source,
    IReadOnlyDictionary<string, string> Dimensions)
{
    public static SubjectFacts Of(decimal amount, string currency = "SAR", Guid? vendorId = null, string source = "Manual",
        IReadOnlyDictionary<string, string>? dimensions = null) =>
        new(amount, currency, vendorId, source, dimensions ?? new Dictionary<string, string>());
}

public static class Conditions
{
    public const NumberStyles AmountStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
    public const string DimensionPrefix = "dimension:";
    private static readonly string[] SimpleFields = ["amount", "currency", "vendor", "source"];

    public static bool IsKnownField(string field) =>
        SimpleFields.Contains(field) || (field.StartsWith(DimensionPrefix, StringComparison.Ordinal) && field.Length > DimensionPrefix.Length);

    public static bool Matches(IReadOnlyList<ClauseSpec> clauses, SubjectFacts facts) => clauses.All(c => Matches(c, facts));

    public static bool Matches(ClauseSpec clause, SubjectFacts facts)
    {
        if (clause.Field == "amount")
        {
            var amounts = clause.Values.Select(v => decimal.Parse(v, AmountStyle, CultureInfo.InvariantCulture)).ToList();
            return clause.Op switch
            {
                ConditionOp.Eq => facts.Amount == amounts[0],
                ConditionOp.Ne => facts.Amount != amounts[0],
                ConditionOp.Gt => facts.Amount > amounts[0],
                ConditionOp.Ge => facts.Amount >= amounts[0],
                ConditionOp.Lt => facts.Amount < amounts[0],
                ConditionOp.Le => facts.Amount <= amounts[0],
                ConditionOp.In => amounts.Contains(facts.Amount),
                _ => false,
            };
        }

        var actual = clause.Field switch
        {
            "currency" => facts.Currency,
            "vendor" => facts.VendorId?.ToString(),
            "source" => facts.Source,
            _ when clause.Field.StartsWith(DimensionPrefix, StringComparison.Ordinal) =>
                facts.Dimensions.TryGetValue(clause.Field[DimensionPrefix.Length..], out var d) ? d : null,
            _ => null,
        };

        bool Same(string value) => actual is not null && string.Equals(actual, value, StringComparison.OrdinalIgnoreCase);
        return clause.Op switch
        {
            ConditionOp.Eq => Same(clause.Values[0]),
            ConditionOp.Ne => !Same(clause.Values[0]),
            ConditionOp.In => clause.Values.Any(Same),
            _ => false,
        };
    }
}
