using System.Text.Json;
using Vpms.Payments.Application;
using Vpms.Payments.Domain;
using Vpms.Payments.Infrastructure;

namespace Vpms.UnitTests.Payments;

public class ReleaseSeparationAndExportTests
{
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Submitter = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly Guid InvoiceApprover = Guid.NewGuid();
    private static readonly Guid Releaser = Guid.NewGuid();

    private static (PaymentProposal, List<PaymentRequest>) Setup()
    {
        var p = new PaymentProposal { CreatedBy = Maker, SubmittedBy = Submitter, ApprovedBy = Approver };
        var r = new PaymentRequest { Involved = [InvoiceApprover, Guid.NewGuid()] };
        return (p, [r]);
    }

    [Fact]
    public void A_stranger_may_release()
    {
        var (p, r) = Setup();
        Assert.Empty(ReleaseSeparation.Evaluate(Releaser, p, r));
    }

    [Theory]
    [InlineData("maker", "releaser_is_maker")]
    [InlineData("submitter", "releaser_is_submitter")]
    [InlineData("approver", "releaser_is_approver")]
    [InlineData("invoice", "releaser_took_part_in_invoice_or_bank_details")]
    public void Everyone_who_took_part_is_refused(string who, string code)
    {
        var (p, r) = Setup();
        var person = who switch { "maker" => Maker, "submitter" => Submitter, "approver" => Approver, _ => InvoiceApprover };
        Assert.Equal([code], ReleaseSeparation.Evaluate(person, p, r).Select(f => f.Code));
    }

    [Fact]
    public void Someone_with_several_roles_gets_every_reason()
    {
        var p = new PaymentProposal { CreatedBy = Maker, SubmittedBy = Maker, ApprovedBy = Approver };
        var r = new PaymentRequest { Involved = [Maker] };
        Assert.Equal(3, ReleaseSeparation.Evaluate(Maker, p, [r]).Count);
    }

    private static InstructionModel Model(string name = "Al Amal Trading", string invoice = "INV-1") => new(
        Guid.NewGuid(), "PP-2026-000001", Guid.NewGuid(), "Acme KSA", "SAR", 345.5m, new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero), Guid.NewGuid(),
        [new InstructionPayment("PP-2026-000001-001", name, "Example Bank", "EXMPSARI", "SA", "SA0380000000608010167519", null, "SAR", 345.5m, invoice, new DateOnly(2026, 11, 1))]);

    [Fact]
    public void The_csv_has_a_header_and_one_quoted_row_per_payment()
    {
        var (csv, _) = new CsvJsonInstructionExporter().Export(Model());
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Reference,BeneficiaryName,", lines[0]);
        Assert.Contains("\"SA0380000000608010167519\"", lines[1]);
        Assert.Contains("\"345.50\"", lines[1]);
        Assert.Contains("\"2026-11-01\"", lines[1]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1+1", "\"'+1+1\"")]
    [InlineData("-2+3", "\"'-2+3\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("Plain, with comma", "\"Plain, with comma\"")]
    [InlineData("Quote \"inside\"", "\"Quote \"\"inside\"\"\"")]
    public void Cells_are_quoted_and_formulas_defused(string value, string expected) =>
        Assert.Equal(expected, CsvJsonInstructionExporter.Cell(value));

    [Fact]
    public void A_vendor_name_that_looks_like_a_formula_cannot_run_in_a_spreadsheet()
    {
        var (csv, _) = new CsvJsonInstructionExporter().Export(Model(name: "=cmd|' /C calc'!A0"));
        Assert.Contains("\"'=cmd|' /C calc'!A0\"", csv);
    }

    [Fact]
    public void The_json_carries_the_same_payment_in_structured_form()
    {
        var m = Model();
        var (_, json) = new CsvJsonInstructionExporter().Export(m);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("CSV+JSON v1", root.GetProperty("format").GetString());
        Assert.Equal(m.InstructionId, root.GetProperty("instructionId").GetGuid());
        var payment = root.GetProperty("payments")[0];
        Assert.Equal("SA0380000000608010167519", payment.GetProperty("beneficiary").GetProperty("iban").GetString());
        Assert.Equal(345.5m, payment.GetProperty("amount").GetDecimal());
        Assert.Equal("INV-1", payment.GetProperty("invoiceNumber").GetString());
    }

    [Fact]
    public void The_same_content_always_gives_the_same_hash_and_any_change_alters_it()
    {
        var a = TreasuryService.HashOf("csv", "json");
        Assert.Equal(a, TreasuryService.HashOf("csv", "json"));
        Assert.NotEqual(a, TreasuryService.HashOf("csv ", "json"));
        Assert.NotEqual(a, TreasuryService.HashOf("csv", "jsOn"));
        Assert.NotEqual(TreasuryService.HashOf("ab", "c"), TreasuryService.HashOf("a", "bc"));
        Assert.Equal(64, a.Length);
    }
}
