using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.ErpIntegration.Domain;
using Vpms.ErpIntegration.Infrastructure;

namespace Vpms.UnitTests.ErpIntegration;

public sealed class ReconciliationItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    private static ReconciliationItem Item(string type = ReconType.AmountMismatch) => new() { Id = Guid.NewGuid(), Type = type, Status = ReconItemStatus.Open };

    [Fact]
    public void The_key_changes_when_either_value_changes_and_not_otherwise()
    {
        var a = ReconciliationItem.KeyFor(ReconType.AmountMismatch, "inv1", "100.00 SAR", "90.00 SAR");
        Assert.Equal(a, ReconciliationItem.KeyFor(ReconType.AmountMismatch, "inv1", "100.00 SAR", "90.00 SAR"));
        Assert.NotEqual(a, ReconciliationItem.KeyFor(ReconType.AmountMismatch, "inv1", "100.00 SAR", "95.00 SAR"));
        Assert.NotEqual(a, ReconciliationItem.KeyFor(ReconType.AmountMismatch, "inv1", "110.00 SAR", "90.00 SAR"));
        Assert.NotEqual(a, ReconciliationItem.KeyFor(ReconType.VendorMismatch, "inv1", "100.00 SAR", "90.00 SAR"));
    }

    [Fact]
    public void Resolving_needs_a_reason_and_only_works_on_an_open_item()
    {
        var item = Item(ReconType.MissingInErp);
        Assert.Throws<ValidationFailedException>(() => item.Resolve(Maker, " ", Now));
        item.Resolve(Maker, "Posted by hand", Now);
        Assert.Equal(ReconItemStatus.Resolved, item.Status);
        Assert.Equal(Maker, item.DecidedBy);
        Assert.Throws<ConflictException>(() => item.Resolve(Maker, "again", Now));
    }

    [Theory]
    [InlineData(ReconType.MissingInErp)]
    [InlineData(ReconType.StatusMismatch)]
    [InlineData(ReconType.UnknownInErp)]
    [InlineData(ReconType.PaidInErpNotReleased)]
    public void Accepting_other_differences_takes_one_person_and_a_reason(string type)
    {
        var item = Item(type);
        Assert.Throws<ValidationFailedException>(() => item.ProposeAcceptance(Maker, "", Now));
        item.ProposeAcceptance(Maker, "Known: entered directly by treasury", Now);
        Assert.Equal(ReconItemStatus.Accepted, item.Status);
    }

    [Theory]
    [InlineData(ReconType.AmountMismatch)]
    [InlineData(ReconType.VendorMismatch)]
    public void Accepting_a_money_or_payee_difference_needs_a_different_person_to_confirm(string type)
    {
        var item = Item(type);
        item.ProposeAcceptance(Maker, "Rounding agreed with the supplier", Now);
        Assert.Equal(ReconItemStatus.PendingAcceptance, item.Status);
        Assert.Equal(Maker, item.ProposedBy);

        var ex = Assert.Throws<SegregationOfDutiesException>(() => item.ApproveAcceptance(Maker, null, Now));
        Assert.Equal("maker-checker", ex.Rule);
        Assert.Equal(ReconItemStatus.PendingAcceptance, item.Status);

        item.ApproveAcceptance(Checker, null, Now);
        Assert.Equal(ReconItemStatus.Accepted, item.Status);
        Assert.Equal(Checker, item.DecidedBy);
        Assert.Equal("Rounding agreed with the supplier", item.DecisionReason);
    }

    [Fact]
    public void A_rejected_acceptance_reopens_the_item_and_needs_a_reason_from_a_different_person()
    {
        var item = Item();
        item.ProposeAcceptance(Maker, "ok", Now);
        Assert.Throws<SegregationOfDutiesException>(() => item.RejectAcceptance(Maker, "no", Now));
        Assert.Throws<ValidationFailedException>(() => item.RejectAcceptance(Checker, "", Now));
        item.RejectAcceptance(Checker, "Not agreed", Now);
        Assert.Equal(ReconItemStatus.Open, item.Status);
        Assert.Null(item.ProposedBy);
        Assert.Equal("Not agreed", item.DecisionReason);
    }

    [Fact]
    public void Acceptance_cannot_be_approved_when_nothing_was_proposed_or_proposed_twice()
    {
        var item = Item();
        Assert.Throws<ConflictException>(() => item.ApproveAcceptance(Checker, null, Now));
        item.ProposeAcceptance(Maker, "x", Now);
        Assert.Throws<ConflictException>(() => item.ProposeAcceptance(Checker, "y", Now));
    }

    [Fact]
    public void A_difference_that_disappears_is_cleared_but_a_decided_one_is_left_alone()
    {
        var open = Item();
        open.Clear(Now);
        Assert.Equal(ReconItemStatus.Cleared, open.Status);

        var accepted = Item(ReconType.MissingInErp);
        accepted.ProposeAcceptance(Maker, "known", Now);
        accepted.Clear(Now);
        Assert.Equal(ReconItemStatus.Accepted, accepted.Status);
    }

    [Theory]
    [InlineData("=SUM(A1)")]
    [InlineData("+1+1")]
    [InlineData("-2")]
    [InlineData("@cmd")]
    public void Csv_cells_that_look_like_formulas_are_defused(string value) => Assert.Equal("\"'" + value + "\"", ReconciliationService.Cell(value));

    [Fact]
    public void Csv_cells_are_quoted_and_inner_quotes_doubled()
    {
        Assert.Equal("\"a,b\"", ReconciliationService.Cell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ReconciliationService.Cell("say \"hi\""));
        Assert.Equal("\"\"", ReconciliationService.Cell(null));
    }
}
