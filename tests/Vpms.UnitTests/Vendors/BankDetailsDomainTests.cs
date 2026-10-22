using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Vendors.Domain;

namespace Vpms.UnitTests.Vendors;

public class BankDetailsDomainTests
{
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static BankDetailChange NewChange(string source = ChangeSource.Staff, Guid? replaces = null) => new()
    {
        Id = Guid.NewGuid(), MadeBy = Maker, MadeAt = Now, ExpiresAt = Now.AddDays(14), Source = source, ReplacesBankDetailsId = replaces,
    };

    [Fact]
    public void The_maker_can_never_approve_or_reject_their_own_change()
    {
        var c = NewChange();
        Assert.Equal("maker-checker", Assert.Throws<SegregationOfDutiesException>(() => c.Approve(Maker, null, null, Now)).Rule);
        Assert.Throws<SegregationOfDutiesException>(() => c.Reject(Maker, "no", Now));
        Assert.Equal(ChangeStatus.Pending, c.Status);
        Assert.Null(c.CheckedBy);
    }

    [Fact]
    public void A_different_person_approves_a_first_time_account_without_a_callback()
    {
        var c = NewChange();
        Assert.False(c.RequiresCallback);
        c.Approve(Checker, null, " ok ", Now);
        Assert.Equal((ChangeStatus.Approved, Checker, "ok"), (c.Status, c.CheckedBy, c.CheckerComment));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Changes_to_existing_accounts_and_vendor_requests_need_a_recorded_callback(bool replacesExisting, bool fromVendor)
    {
        var c = NewChange(fromVendor ? ChangeSource.VendorPortal : ChangeSource.Staff, replacesExisting ? Guid.NewGuid() : null);
        Assert.True(c.RequiresCallback);
        Assert.Equal("callback_required", Assert.Throws<ValidationFailedException>(() => c.Approve(Checker, "  ", null, Now)).Code);
        c.Approve(Checker, "CB-2026-10-21-0042", null, Now);
        Assert.Equal("CB-2026-10-21-0042", c.CallbackVerificationRef);
    }

    [Fact]
    public void Decided_changes_cannot_be_decided_again()
    {
        var c = NewChange();
        c.Approve(Checker, null, null, Now);
        Assert.Equal("change_not_pending", Assert.Throws<ConflictException>(() => c.Approve(Guid.NewGuid(), null, null, Now)).Code);
        Assert.Throws<ConflictException>(() => c.Reject(Guid.NewGuid(), "late", Now));
        Assert.Throws<ConflictException>(() => c.Cancel(Maker, Now));
    }

    [Fact]
    public void Expired_changes_cannot_be_approved()
    {
        var c = NewChange();
        Assert.True(c.IsStale(Now.AddDays(15)));
        Assert.Equal("change_expired", Assert.Throws<ConflictException>(() => c.Approve(Checker, null, null, Now.AddDays(15))).Code);
        c.Expire();
        Assert.Equal(ChangeStatus.Expired, c.Status);
    }

    [Fact]
    public void Only_the_maker_can_cancel_and_rejection_needs_a_reason()
    {
        var c = NewChange();
        Assert.Equal("not_the_maker", Assert.Throws<ConflictException>(() => c.Cancel(Checker, Now)).Code);
        Assert.Equal("reason_required", Assert.Throws<ValidationFailedException>(() => c.Reject(Checker, " ", Now)).Code);
        c.Cancel(Maker, Now);
        Assert.Equal(ChangeStatus.Cancelled, c.Status);
    }

    [Theory]
    [InlineData("SA0380000000608010167519", true)]
    [InlineData("sa03 8000 0000 6080 1016 7519", true)]
    [InlineData("AE070331234567890123456", true)]
    [InlineData("GB82WEST12345698765432", true)]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("SA0380000000608010167518", false)]
    [InlineData("SA03800000006080101675", false)]
    [InlineData("XX", false)]
    [InlineData("", false)]
    [InlineData("1234567890123456", false)]
    public void Validates_ibans_by_length_and_checksum(string value, bool valid) => Assert.Equal(valid, Iban.IsValid(value));

    [Fact]
    public void Masks_all_but_the_last_four_characters() => Assert.Equal("••••7519", Iban.Mask("SA0380000000608010167519"));
}
