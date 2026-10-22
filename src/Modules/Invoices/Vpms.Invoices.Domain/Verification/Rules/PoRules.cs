using System.Globalization;

namespace Vpms.Invoices.Domain.Verification.Rules;

public sealed class PoRequiredRule : IVerificationRule
{
    public string Code => RuleCodes.PoRequired;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting) =>
        string.IsNullOrWhiteSpace(input.Invoice.PoReference) ? [Finding.Of("po_reference_missing")] : [];
}

/// <summary>Two-way match: the invoice against the purchase order (price, quantity and total value). Goods receipts arrive with the ERP.</summary>
public sealed class PoMatchRule : IVerificationRule
{
    public string Code => RuleCodes.PoMatch;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting)
    {
        var invoice = input.Invoice;
        if (string.IsNullOrWhiteSpace(invoice.PoReference)) return []; // po.required decides whether that is acceptable

        var po = input.PurchaseOrder;
        if (po is null) return [Finding.Of("po_not_found", ("poNumber", invoice.PoReference))];

        var findings = new List<Finding>();
        if (po.VendorId != invoice.VendorId) findings.Add(Finding.Of("po_other_vendor", ("poNumber", po.PoNumber)));
        if (po.CurrencyCode != invoice.CurrencyCode) findings.Add(Finding.Of("po_currency_mismatch", ("poNumber", po.PoNumber), ("poCurrency", po.CurrencyCode), ("invoiceCurrency", invoice.CurrencyCode)));
        if (po.Status != "Open") findings.Add(Finding.Of("po_not_open", ("poNumber", po.PoNumber), ("status", po.Status)));

        var pricePercent = setting.Parameters["priceTolerancePercent"];
        var priceAbsolute = setting.Parameters["priceToleranceAbsolute"];
        var quantityPercent = setting.Parameters["quantityTolerancePercent"];
        var overBillingPercent = setting.Parameters["overBillingPercent"];

        foreach (var line in invoice.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.PoLineRef))
            {
                findings.Add(Finding.Of("line_without_po_line", ("lineNo", line.LineNo.ToString(CultureInfo.InvariantCulture))));
                continue;
            }

            var poLine = po.Lines.FirstOrDefault(l => l.LineNo == line.PoLineRef.Trim());
            if (poLine is null)
            {
                findings.Add(Finding.Of("po_line_not_found", ("lineNo", line.LineNo.ToString(CultureInfo.InvariantCulture)), ("poLine", line.PoLineRef)));
                continue;
            }

            var allowedPriceDiff = Math.Max(priceAbsolute, poLine.UnitPrice * pricePercent / 100m);
            if (Math.Abs(line.UnitPrice - poLine.UnitPrice) > allowedPriceDiff)
            {
                findings.Add(Finding.Of("po_price_mismatch", ("lineNo", line.LineNo.ToString(CultureInfo.InvariantCulture)), ("poLine", poLine.LineNo),
                    ("invoicePrice", line.UnitPrice.ToString("F4", CultureInfo.InvariantCulture)), ("poPrice", poLine.UnitPrice.ToString("F4", CultureInfo.InvariantCulture))));
            }

            // Quantity: what earlier invoices already used plus this line must stay within the order (plus tolerance).
            var invoicedBefore = poLine.PreviouslyInvoicedQuantity
                                 + invoice.Lines.Where(other => other.LineNo < line.LineNo && other.PoLineRef?.Trim() == poLine.LineNo).Sum(other => other.Quantity);
            var allowedQuantity = poLine.OrderedQuantity * (1m + quantityPercent / 100m);
            if (invoicedBefore + line.Quantity > allowedQuantity)
            {
                findings.Add(Finding.Of("po_quantity_exceeded", ("lineNo", line.LineNo.ToString(CultureInfo.InvariantCulture)), ("poLine", poLine.LineNo),
                    ("ordered", poLine.OrderedQuantity.ToString("F4", CultureInfo.InvariantCulture)),
                    ("invoicedIncludingThis", (invoicedBefore + line.Quantity).ToString("F4", CultureInfo.InvariantCulture))));
            }
        }

        // Value: everything invoiced against the order, including this invoice, against what was ordered.
        if (po.OrderedValue > 0)
        {
            var invoicedValue = po.PreviouslyInvoicedValue + invoice.Subtotal;
            if (invoicedValue > po.OrderedValue * (1m + overBillingPercent / 100m))
            {
                findings.Add(Finding.Of("po_value_exceeded", ("poNumber", po.PoNumber), ("ordered", po.OrderedValue.ToString("F4", CultureInfo.InvariantCulture)),
                    ("invoicedIncludingThis", invoicedValue.ToString("F4", CultureInfo.InvariantCulture))));
            }
        }

        return findings;
    }
}
