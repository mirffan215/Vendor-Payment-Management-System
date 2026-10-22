using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Payments.Domain;

namespace Vpms.UnitTests.Payments;

public class PaymentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static PaymentRequest Request() => new() { Id = Guid.NewGuid(), Amount = 100, CurrencyCode = "SAR", CreatedBy = Maker };

    private static PaymentProposal Proposal(int items = 1)
    {
        var p = new PaymentProposal { Id = Guid.NewGuid(), CreatedBy = Maker };
        for (var i = 0; i < items; i++) p.Items.Add(new PaymentProposalItem { Id = Guid.NewGuid(), PaymentRequestId = Guid.NewGuid(), Amount = 50m });
        p.Recalculate();
        return p;
    }

    [Fact]
    public void A_request_moves_into_a_proposal_and_back_to_the_pool()
    {
        var r = Request();
        var proposal = Guid.NewGuid();
        r.AddToProposal(proposal);
        Assert.Equal(RequestStatus.InProposal, r.Status);
        Assert.Equal(proposal, r.ProposalId);
        Assert.Throws<ConflictException>(() => r.AddToProposal(Guid.NewGuid()));

        r.ReturnToPool();
        Assert.Equal(RequestStatus.Created, r.Status);
        Assert.Null(r.ProposalId);
    }

    [Fact]
    public void Only_a_request_outside_any_proposal_can_be_cancelled_and_it_needs_a_reason()
    {
        var r = Request();
        Assert.Equal("reason_required", Assert.Throws<ValidationFailedException>(() => r.Cancel(" ", Now)).Code);
        r.AddToProposal(Guid.NewGuid());
        Assert.Throws<ConflictException>(() => r.Cancel("no longer needed", Now));
        r.ReturnToPool();
        r.Cancel("no longer needed", Now);
        Assert.Equal(RequestStatus.Cancelled, r.Status);
    }

    [Fact]
    public void A_released_request_can_never_be_invalidated_or_returned()
    {
        var r = Request();
        r.AddToProposal(Guid.NewGuid());
        r.MarkReleased(Now);
        Assert.Throws<ConflictException>(() => r.Invalidate("invoice changed", Now));
        r.ReturnToPool();
        Assert.Equal(RequestStatus.Released, r.Status);
    }

    [Fact]
    public void An_invalidated_request_keeps_its_reason()
    {
        var r = Request();
        r.Invalidate("bank details changed", Now);
        Assert.Equal(RequestStatus.Invalidated, r.Status);
        Assert.Equal("bank details changed", r.StatusReason);
        Assert.DoesNotContain(r.Status, RequestStatus.Open);
    }

    [Fact]
    public void A_proposal_goes_draft_submitted_approved_released()
    {
        var p = Proposal(2);
        Assert.Equal(100m, p.TotalAmount);
        p.Submit(Maker, Now);
        p.Approve(Other, "ok", Now);
        Assert.Equal(Other, p.ApprovedBy);
        p.MarkReleased(Guid.NewGuid(), Now);
        Assert.Equal(ProposalStatus.Released, p.Status);
        Assert.Throws<ConflictException>(() => p.Cancel("too late", Now));
        Assert.Throws<ConflictException>(() => p.Invalidate("too late", Now));
    }

    [Fact]
    public void The_maker_and_the_submitter_cannot_approve()
    {
        var p = Proposal();
        p.Submit(Other, Now);
        Assert.Equal("maker-checker", Assert.Throws<SegregationOfDutiesException>(() => p.Approve(Maker, null, Now)).Rule);
        Assert.Equal("maker-checker", Assert.Throws<SegregationOfDutiesException>(() => p.Approve(Other, null, Now)).Rule);
        p.Approve(Guid.NewGuid(), null, Now);
        Assert.Equal(ProposalStatus.Approved, p.Status);
    }

    [Fact]
    public void An_empty_proposal_cannot_be_submitted_and_release_needs_approval()
    {
        Assert.Equal("empty_proposal", Assert.Throws<ConflictException>(() => Proposal(0).Submit(Maker, Now)).Code);
        var p = Proposal();
        Assert.Throws<ConflictException>(() => p.MarkReleased(Other, Now));
        p.Submit(Maker, Now);
        Assert.Throws<ConflictException>(() => p.MarkReleased(Other, Now));
    }

    [Fact]
    public void Sending_back_needs_a_reason_and_returns_to_draft()
    {
        var p = Proposal();
        p.Submit(Maker, Now);
        Assert.Throws<ValidationFailedException>(() => p.SendBack(Other, "", Now));
        Assert.Throws<SegregationOfDutiesException>(() => p.SendBack(Maker, "x", Now));
        p.SendBack(Other, "wrong due date", Now);
        Assert.Equal(ProposalStatus.Draft, p.Status);
        Assert.Null(p.SubmittedBy);
        Assert.Equal("wrong due date", p.StatusReason);
    }

    [Fact]
    public void Invalidating_or_cancelling_a_live_proposal_closes_it()
    {
        foreach (var advance in new Action<PaymentProposal>[] { _ => { }, p => p.Submit(Maker, Now), p => { p.Submit(Maker, Now); p.Approve(Other, null, Now); } })
        {
            var p = Proposal();
            advance(p);
            p.Invalidate("invoice changed", Now);
            Assert.Equal(ProposalStatus.Invalidated, p.Status);
            Assert.NotNull(p.ClosedAt);
        }

        var c = Proposal();
        Assert.Throws<ValidationFailedException>(() => c.Cancel("", Now));
        c.Cancel("not needed", Now);
        Assert.Equal(ProposalStatus.Cancelled, c.Status);
    }

    [Fact]
    public void Proposal_numbers_are_readable_and_involved_people_round_trip()
    {
        Assert.Equal("PP-2026-000042", PaymentProposal.FormatNo(42, Now));
        var a = Guid.NewGuid();
        var r = Request();
        r.Involved = [a, a, Other];
        Assert.Equal([a, Other], r.Involved);
    }

    [Fact]
    public void A_handed_off_instruction_stays_handed_off_to_the_first_person()
    {
        var i = new PaymentInstruction();
        i.HandOff(Maker, Now);
        i.HandOff(Other, Now.AddHours(1));
        Assert.Equal(InstructionStatus.HandedOff, i.Status);
        Assert.Equal(Maker, i.HandedOffBy);
    }
}
