using Vpms.BuildingBlocks.Errors;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Infrastructure;

namespace Vpms.UnitTests.Invoices;

public class MoneyTests
{
    [Theory]
    [InlineData("SAR", 2)]
    [InlineData("USD", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    [InlineData("BHD", 3)]
    public void Currencies_have_the_right_number_of_decimals(string currency, int places) =>
        Assert.Equal(places, Money.DecimalPlaces(currency));

    [Theory]
    [InlineData("SAR", 1.005, 1.01)]   // half away from zero, not banker's rounding
    [InlineData("SAR", 1.015, 1.02)]
    [InlineData("SAR", 2.675, 2.68)]
    [InlineData("SAR", 0.004, 0.00)]
    [InlineData("JPY", 100.5, 101)]
    [InlineData("KWD", 1.0005, 1.001)]
    public void Rounds_half_away_from_zero(string currency, double amount, double expected) =>
        Assert.Equal((decimal)expected, Money.Round((decimal)amount, currency));

    [Theory]
    [InlineData("SAR", 0.01)]
    [InlineData("JPY", 1)]
    [InlineData("KWD", 0.001)]
    public void Minor_unit_matches_the_currency(string currency, double unit) =>
        Assert.Equal((decimal)unit, Money.MinorUnit(currency));
}

public class InvoiceNumberTests
{
    [Theory]
    [InlineData("INV-001", "INV001")]
    [InlineData("inv 001", "INV001")]
    [InlineData("INV/0001", "INV0001")]
    [InlineData("  a.b-c ", "ABC")]
    [InlineData("فاتورة-12", "فاتورة12")]
    public void Normalize_keeps_only_letters_and_digits_in_upper_case(string input, string expected) =>
        Assert.Equal(expected, InvoiceNumber.Normalize(input));

    [Theory]
    [InlineData("INV-0001", "INV1")]
    [InlineData("INV-1", "INV1")]
    [InlineData("inv/001", "INV1")]
    [InlineData("0042", "42")]
    [InlineData("000", "0")]
    [InlineData("A10B007", "A10B7")]
    [InlineData("INV-100", "INV100")]   // zeros inside a number are kept
    public void Canonical_ignores_leading_zeros_of_digit_runs(string input, string expected) =>
        Assert.Equal(expected, InvoiceNumber.Canonical(input));

    [Fact]
    public void Different_numbers_stay_different() =>
        Assert.NotEqual(InvoiceNumber.Canonical("INV-10"), InvoiceNumber.Canonical("INV-1"));
}

public class InvoiceLifecycleTests
{
    private static Invoice NewInvoice(string status = InvoiceStatus.Draft) => new() { Id = Guid.NewGuid(), Status = status };
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Happy_path_walks_through_every_status()
    {
        var i = NewInvoice();
        i.Submit(Now);
        Assert.Equal(InvoiceStatus.Received, i.Status);
        Assert.Equal(Now, i.ReceivedAt);
        i.StartVerification();
        i.MarkVerified();
        i.MarkReadyForApproval();
        Assert.Equal(InvoiceStatus.ReadyForApproval, i.Status);
    }

    [Fact]
    public void Exception_goes_back_to_received_when_the_invoice_is_edited()
    {
        var i = NewInvoice(InvoiceStatus.InVerification);
        i.MarkException("duplicate");
        Assert.Equal(InvoiceStatus.Exception, i.Status);
        var editor = Guid.NewGuid();
        i.BeginEdit(editor, Now);
        Assert.Equal(InvoiceStatus.Received, i.Status);
        Assert.Equal(2, i.Version);
        Assert.Equal(editor, i.LastEditedBy);
        Assert.Null(i.StatusReason);
    }

    [Theory]
    [InlineData(InvoiceStatus.Received)]
    [InlineData(InvoiceStatus.InVerification)]
    [InlineData(InvoiceStatus.Verified)]
    [InlineData(InvoiceStatus.ReadyForApproval)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void Only_drafts_and_exceptions_can_be_edited(string status)
    {
        var i = NewInvoice(status);
        Assert.False(i.IsEditable);
        Assert.Equal("invalid_transition", Assert.Throws<ConflictException>(() => i.BeginEdit(Guid.NewGuid(), Now)).Code);
    }

    [Theory]
    [InlineData(InvoiceStatus.Received)]
    [InlineData(InvoiceStatus.InVerification)]
    [InlineData(InvoiceStatus.Exception)]
    [InlineData(InvoiceStatus.Verified)]
    [InlineData(InvoiceStatus.ReadyForApproval)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void Only_a_draft_can_be_submitted(string status) =>
        Assert.Throws<ConflictException>(() => NewInvoice(status).Submit(Now));

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Exception)]
    [InlineData(InvoiceStatus.Verified)]
    [InlineData(InvoiceStatus.ReadyForApproval)]
    public void Verification_only_starts_from_received(string status) =>
        Assert.Throws<ConflictException>(() => NewInvoice(status).StartVerification());

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Received)]
    [InlineData(InvoiceStatus.Exception)]
    [InlineData(InvoiceStatus.Verified)]
    public void Invoices_can_be_cancelled_with_a_reason_until_they_go_to_approval(string status)
    {
        var i = NewInvoice(status);
        i.Cancel("entered twice");
        Assert.Equal(InvoiceStatus.Cancelled, i.Status);
        Assert.Equal("entered twice", i.StatusReason);
    }

    [Theory]
    [InlineData(InvoiceStatus.InVerification)]
    [InlineData(InvoiceStatus.ReadyForApproval)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void Cancelling_is_refused_in_other_statuses(string status) =>
        Assert.Throws<ConflictException>(() => NewInvoice(status).Cancel("x"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Cancel_and_reject_need_a_reason(string reason)
    {
        Assert.Equal("reason_required", Assert.Throws<ValidationFailedException>(() => NewInvoice().Cancel(reason)).Code);
        Assert.Equal("reason_required", Assert.Throws<ValidationFailedException>(() => NewInvoice(InvoiceStatus.Exception).Reject(reason)).Code);
    }

    [Fact]
    public void Terminal_invoices_cannot_come_back()
    {
        var i = NewInvoice(InvoiceStatus.Exception);
        i.Reject("not ours");
        Assert.Throws<ConflictException>(() => i.Submit(Now));
        Assert.Throws<ConflictException>(() => i.BeginEdit(Guid.NewGuid(), Now));
        Assert.Throws<ConflictException>(() => i.Cancel("x"));
    }
}

public class InvoiceValidationTests
{
    private static InvoiceLineInput Line(string desc = "Item", decimal tax = 0.15m) => new(desc, 1, 100m, 100m, "S", tax, 15m, null, null);

    private static InvoiceData Valid(Func<InvoiceData, InvoiceData>? change = null)
    {
        var d = new InvoiceData("INV-1", new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "SAR", 100m, 15m, 115m, "PO-1", null, [Line()]);
        return change is null ? d : change(d);
    }

    private static string Code(InvoiceData d) => Assert.Throws<ValidationFailedException>(() => InvoiceService.Validate(d)).Code;

    [Fact]
    public void A_normal_invoice_passes() => InvoiceService.Validate(Valid());

    [Fact]
    public void An_invoice_without_lines_passes_shape_validation_because_totals_are_a_verification_matter() =>
        InvoiceService.Validate(Valid(d => d with { Lines = [] }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void Invoice_numbers_need_letters_or_digits(string number) =>
        Assert.Equal("invalid_invoice_number", Code(Valid(d => d with { InvoiceNumber = number })));

    [Fact]
    public void Invoice_numbers_are_limited_to_100_characters() =>
        Assert.Equal("invalid_invoice_number", Code(Valid(d => d with { InvoiceNumber = new string('A', 101) })));

    [Theory]
    [InlineData("sar")]
    [InlineData("SA")]
    [InlineData("SARR")]
    [InlineData("")]
    public void Currency_must_be_three_capital_letters(string currency) =>
        Assert.Equal("invalid_currency", Code(Valid(d => d with { CurrencyCode = currency })));

    [Theory]
    [InlineData(1999)]
    [InlineData(2101)]
    public void Dates_must_be_plausible(int year)
    {
        Assert.Equal("invalid_date", Code(Valid(d => d with { InvoiceDate = new DateOnly(year, 1, 1) })));
        Assert.Equal("invalid_date", Code(Valid(d => d with { DueDate = new DateOnly(year, 1, 1) })));
    }

    [Fact]
    public void Header_amounts_cannot_be_negative()
    {
        Assert.Equal("invalid_amount", Code(Valid(d => d with { Subtotal = -1 })));
        Assert.Equal("invalid_amount", Code(Valid(d => d with { TaxTotal = -1 })));
        Assert.Equal("invalid_amount", Code(Valid(d => d with { Total = -0.01m })));
    }

    [Fact]
    public void Lines_need_a_description_and_non_negative_amounts()
    {
        Assert.Equal("invalid_line", Code(Valid(d => d with { Lines = [Line(" ")] })));
        Assert.Equal("invalid_line", Code(Valid(d => d with { Lines = [Line(new string('x', 501))] })));
        Assert.Equal("invalid_amount", Code(Valid(d => d with { Lines = [Line() with { Quantity = -1 }] })));
        Assert.Equal("invalid_amount", Code(Valid(d => d with { Lines = [Line() with { UnitPrice = -1 }] })));
        Assert.Equal("invalid_amount", Code(Valid(d => d with { Lines = [Line() with { TaxRate = 1.5m }] })));
    }

    [Fact]
    public void Dimensions_must_be_a_small_json_object()
    {
        InvoiceService.Validate(Valid(d => d with { Lines = [Line() with { DimensionsJson = "{\"costCenter\":\"CC1\"}" }] }));
        Assert.Equal("invalid_json", Code(Valid(d => d with { Lines = [Line() with { DimensionsJson = "[1,2]" }] })));
        Assert.Equal("invalid_json", Code(Valid(d => d with { Lines = [Line() with { DimensionsJson = "not json" }] })));
    }

    [Fact]
    public void The_number_of_lines_is_capped()
    {
        var many = Enumerable.Range(0, InvoiceService.MaxLines + 1).Select(_ => Line()).ToList();
        Assert.Equal("too_many_lines", Code(Valid(d => d with { Lines = many })));
    }
}
