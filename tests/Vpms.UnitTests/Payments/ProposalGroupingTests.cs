using Vpms.Payments.Domain;

namespace Vpms.UnitTests.Payments;

public class ProposalGroupingTests
{
    private static readonly Guid E1 = Guid.Parse("00000000-0000-0000-0000-00000000e001");
    private static readonly Guid V1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid V2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private static PaymentRequest R(Guid vendor, string currency, int day, string number, string status = RequestStatus.Created, Guid? entity = null) => new()
    {
        Id = Guid.NewGuid(), LegalEntityId = entity ?? E1, VendorId = vendor, CurrencyCode = currency, DueDate = new DateOnly(2026, 11, day), InvoiceNumber = number, Status = status,
    };

    [Fact]
    public void Requests_with_the_same_vendor_currency_and_due_date_share_a_group()
    {
        var groups = ProposalGrouping.Group([R(V1, "SAR", 10, "B"), R(V1, "SAR", 10, "A"), R(V1, "SAR", 10, "C")]);
        var g = Assert.Single(groups);
        Assert.Equal(["A", "B", "C"], g.Requests.Select(r => r.InvoiceNumber));
    }

    [Fact]
    public void A_different_vendor_currency_due_date_or_entity_makes_a_different_group()
    {
        var groups = ProposalGrouping.Group([
            R(V1, "SAR", 10, "1"), R(V2, "SAR", 10, "2"), R(V1, "USD", 10, "3"), R(V1, "SAR", 11, "4"), R(V1, "SAR", 10, "5", entity: Guid.NewGuid()),
        ]);
        Assert.Equal(5, groups.Count);
    }

    [Fact]
    public void Groups_come_in_due_date_order_and_only_open_unproposed_requests_are_grouped()
    {
        var groups = ProposalGrouping.Group([
            R(V1, "SAR", 20, "late"), R(V1, "SAR", 5, "early"), R(V1, "SAR", 5, "in-proposal", RequestStatus.InProposal),
            R(V1, "SAR", 5, "cancelled", RequestStatus.Cancelled), R(V1, "SAR", 5, "released", RequestStatus.Released),
        ]);
        Assert.Equal([5, 20], groups.Select(g => g.Key.DueDate.Day));
        Assert.Equal(["early"], groups[0].Requests.Select(r => r.InvoiceNumber));
    }

    [Fact]
    public void Nothing_to_group_gives_no_groups()
    {
        Assert.Empty(ProposalGrouping.Group([]));
    }

    [Fact]
    public void Findings_that_invalidate_are_told_apart_from_those_that_only_block()
    {
        var invalidating = new RecheckResult([new RecheckFinding(Guid.NewGuid(), Guid.NewGuid(), "invoice_changed", RecheckSeverity.Invalidates)]);
        var blocking = new RecheckResult([new RecheckFinding(Guid.NewGuid(), Guid.NewGuid(), "bank_change_pending", RecheckSeverity.Blocks)]);
        Assert.False(invalidating.Passed);
        Assert.True(invalidating.Invalidates);
        Assert.False(blocking.Passed);
        Assert.False(blocking.Invalidates);
        Assert.True(new RecheckResult([]).Passed);
    }
}
