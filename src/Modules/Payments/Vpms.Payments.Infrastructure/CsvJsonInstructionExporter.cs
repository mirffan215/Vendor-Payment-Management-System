using System.Globalization;
using System.Text;
using System.Text.Json;
using Vpms.Payments.Application;

namespace Vpms.Payments.Infrastructure;

/// <summary>The generic hand-off format: a CSV a person can open, and a structured JSON for an integration. Neither is a bank file.</summary>
public sealed class CsvJsonInstructionExporter : IInstructionExporter
{
    public const string FormatName = "CSV+JSON v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Format => FormatName;

    public (string Csv, string Json) Export(InstructionModel m)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Reference,BeneficiaryName,BankName,SwiftBic,CountryCode,Iban,AccountNumber,Currency,Amount,InvoiceNumber,DueDate,ProposalNo,InstructionId");
        foreach (var p in m.Payments)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                p.Reference, p.BeneficiaryName, p.BankName, p.SwiftBic ?? "", p.CountryCode, p.Iban ?? "", p.AccountNumber ?? "", p.CurrencyCode,
                p.Amount.ToString("0.00##", CultureInfo.InvariantCulture), p.InvoiceNumber, p.DueDate.ToString("yyyy-MM-dd"), m.ProposalNo, m.InstructionId.ToString(),
            }.Select(Cell)));
        }

        var json = JsonSerializer.Serialize(new
        {
            format = FormatName,
            instructionId = m.InstructionId,
            proposalNo = m.ProposalNo,
            legalEntityId = m.LegalEntityId,
            legalEntityName = m.LegalEntityName,
            currency = m.CurrencyCode,
            total = m.TotalAmount,
            preparedAt = m.PreparedAt,
            releasedBy = m.ReleasedBy,
            payments = m.Payments.Select(p => new
            {
                reference = p.Reference,
                beneficiary = new { name = p.BeneficiaryName, bank = p.BankName, swiftBic = p.SwiftBic, country = p.CountryCode, iban = p.Iban, accountNumber = p.AccountNumber },
                currency = p.CurrencyCode,
                amount = p.Amount,
                invoiceNumber = p.InvoiceNumber,
                dueDate = p.DueDate,
            }),
        }, Json);
        return (csv.ToString(), json);
    }

    /// <summary>Quotes a cell, and defuses spreadsheet formulas (a vendor name starting with '=' must not run when the file is opened).</summary>
    public static string Cell(string value)
    {
        var v = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
