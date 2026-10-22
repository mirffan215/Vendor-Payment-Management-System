using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Vendors.Domain;

namespace Vpms.UnitTests.Vendors;

public class VendorLifecycleTests
{
    private static readonly Guid Creator = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Vendor New() => new() { Id = Guid.NewGuid(), CreatedBy = Creator, CreatedAt = Now };

    private static Vendor UnderReview(Guid? submitter = null, string kind = ActorKind.Vendor)
    {
        var v = New();
        v.Submit(submitter, kind, Now);
        v.StartReview(Reviewer);
        return v;
    }

    [Fact]
    public void Follows_the_happy_path()
    {
        var v = New();
        Assert.Equal(VendorStatus.Invited, v.Status);
        v.Submit(null, ActorKind.Vendor, Now);
        Assert.Equal((VendorStatus.Submitted, VerificationStatus.Pending), (v.Status, v.VerificationStatus));
        v.StartReview(Reviewer);
        Assert.Equal(VerificationStatus.InReview, v.VerificationStatus);
        v.Approve(Approver, Now);
        Assert.Equal((VendorStatus.Active, VerificationStatus.Verified), (v.Status, v.VerificationStatus));
        Assert.True(v.CanTransact);
    }

    [Fact]
    public void The_creator_cannot_approve_the_vendor_they_created()
    {
        var v = UnderReview();
        var ex = Assert.Throws<SegregationOfDutiesException>(() => v.Approve(Creator, Now));
        Assert.Equal("different-person", ex.Rule);
        Assert.Equal(VendorStatus.UnderReview, v.Status);
    }

    [Fact]
    public void Whoever_edited_the_data_cannot_approve_it()
    {
        var editor = Guid.NewGuid();
        var v = UnderReview();
        v.MarkEdited(editor, Now);
        Assert.Throws<SegregationOfDutiesException>(() => v.Approve(editor, Now));
        v.Approve(Approver, Now);
    }

    [Fact]
    public void A_staff_member_who_submitted_on_behalf_cannot_approve_but_the_vendor_submitting_is_irrelevant()
    {
        var staff = Guid.NewGuid();
        Assert.Throws<SegregationOfDutiesException>(() => UnderReview(staff, ActorKind.Staff).Approve(staff, Now));
        UnderReview(staff, ActorKind.Vendor).Approve(staff, Now); // a vendor user id colliding with a staff id is not a staff submission
    }

    [Fact]
    public void The_reviewer_may_approve()
    {
        var v = UnderReview();
        v.Approve(Reviewer, Now);
        Assert.Equal(VendorStatus.Active, v.Status);
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("StartReview")]
    [InlineData("Suspend")]
    [InlineData("Reactivate")]
    [InlineData("Reject")]
    [InlineData("Reopen")]
    public void Invalid_transitions_are_conflicts_not_silent_changes(string action)
    {
        var v = New(); // Invited
        var ex = Assert.Throws<ConflictException>(() =>
        {
            switch (action)
            {
                case "Approve": v.Approve(Approver, Now); break;
                case "StartReview": v.StartReview(Reviewer); break;
                case "Suspend": v.Suspend(Approver, "reason"); break;
                case "Reactivate": v.Reactivate(Approver); break;
                case "Reject": v.Reject("reason", Now); break;
                case "Reopen": v.Reopen(); break;
            }
        });
        Assert.Equal("invalid_transition", ex.Code);
        Assert.Equal(VendorStatus.Invited, v.Status);
    }

    [Fact]
    public void Cannot_submit_twice_or_approve_without_review()
    {
        var v = New();
        v.Submit(null, ActorKind.Vendor, Now);
        Assert.Throws<ConflictException>(() => v.Submit(null, ActorKind.Vendor, Now));
        Assert.Throws<ConflictException>(() => v.Approve(Approver, Now));
    }

    [Fact]
    public void Rejection_needs_a_reason_and_can_be_reopened_for_resubmission()
    {
        var v = UnderReview();
        Assert.Equal("reason_required", Assert.Throws<ValidationFailedException>(() => v.Reject(" ", Now)).Code);
        v.Reject("Tax certificate expired", Now);
        Assert.Equal(("Rejected", "Tax certificate expired"), (v.Status, v.StatusReason));

        v.Reopen();
        Assert.Equal((VendorStatus.Invited, VerificationStatus.NotStarted), (v.Status, v.VerificationStatus));
        v.Submit(null, ActorKind.Vendor, Now);
    }

    [Fact]
    public void Suspension_needs_a_reason_and_a_different_person_to_lift()
    {
        var suspender = Guid.NewGuid();
        var v = UnderReview();
        v.Approve(Approver, Now);

        Assert.Equal("reason_required", Assert.Throws<ValidationFailedException>(() => v.Suspend(suspender, "")).Code);
        v.Suspend(suspender, "Sanctions screening hit");
        Assert.False(v.CanTransact);

        Assert.Throws<SegregationOfDutiesException>(() => v.Reactivate(suspender));
        v.Reactivate(Approver);
        Assert.True(v.CanTransact);
        Assert.Null(v.StatusReason);
    }

    [Fact]
    public void Only_active_vendors_can_transact()
    {
        foreach (var status in new[] { VendorStatus.Invited, VendorStatus.Submitted, VendorStatus.UnderReview, VendorStatus.Rejected, VendorStatus.Suspended })
        {
            Assert.False(new Vendor { Status = status }.CanTransact, status);
        }
    }
}
