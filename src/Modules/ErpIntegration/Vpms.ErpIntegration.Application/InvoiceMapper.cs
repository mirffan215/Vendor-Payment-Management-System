using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.Invoices.Contracts;

namespace Vpms.ErpIntegration.Application;

public sealed record MappedInvoice(ErpInvoice? Invoice, ConfigIssue? Problem);

/// <summary>
/// Turns an approved invoice into the ERP document using the entity's mapping. Anything the mapping cannot express is a permanent
/// problem with a clear code, never a guess: posting wrong coding to the ledger is worse than not posting.
/// </summary>
public static class InvoiceMapper
{
    public static MappedInvoice Map(PostingInvoice source, string vendorAccount, string documentKey, ErpCompanyMapping mapping)
    {
        var lines = new List<ErpInvoiceLine>();
        foreach (var line in source.Lines)
        {
            string? taxCode = line.TaxCode;
            if (!string.IsNullOrWhiteSpace(taxCode) && mapping.TaxCodeMap.Count > 0)
            {
                if (!mapping.TaxCodeMap.TryGetValue(taxCode, out var mapped)) return Fail("tax_code_unmapped", $"Tax code '{taxCode}' on line {line.LineNo} has no ERP mapping.");
                taxCode = mapped;
            }

            var dimensions = new Dictionary<string, string>();
            foreach (var used in line.Dimensions.Keys)
            {
                if (!mapping.Dimensions.Any(d => string.Equals(d.VpmsDimension, used, StringComparison.OrdinalIgnoreCase)))
                {
                    return Fail("dimension_unmapped", $"Dimension '{used}' on line {line.LineNo} has no ERP mapping.");
                }
            }

            foreach (var map in mapping.Dimensions)
            {
                var given = line.Dimensions.FirstOrDefault(kv => string.Equals(kv.Key, map.VpmsDimension, StringComparison.OrdinalIgnoreCase));
                var value = given.Key is null ? map.DefaultValue : given.Value;
                if (value is null) continue;
                if (given.Key is not null && map.ValueMap.Count > 0)
                {
                    if (!map.ValueMap.TryGetValue(value, out var mappedValue)) return Fail("dimension_value_unmapped", $"Value '{value}' of dimension '{map.VpmsDimension}' on line {line.LineNo} has no ERP mapping.");
                    value = mappedValue;
                }

                dimensions[map.ErpDimension] = value;
            }

            lines.Add(new ErpInvoiceLine(line.LineNo, line.Description, line.Quantity, line.UnitPrice, line.LineAmount, taxCode, dimensions));
        }

        if (lines.Count == 0) return Fail("no_lines", "The invoice has no lines to post.");
        var h = source.Header;
        var invoice = new ErpInvoice(documentKey, vendorAccount, h.InvoiceNumber, h.InvoiceDate, h.DueDate, h.CurrencyCode, h.Total, source.TaxTotal, lines);
        return new MappedInvoice(invoice, null);
    }

    private static MappedInvoice Fail(string code, string message) => new(null, new ConfigIssue(code, message));
}
