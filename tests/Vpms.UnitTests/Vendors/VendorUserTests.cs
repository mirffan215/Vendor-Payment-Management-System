using Vpms.VendorPortal.Domain;

namespace Vpms.UnitTests.Vendors;

public class VendorUserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Locks_after_the_configured_number_of_failures_and_unlocks_by_time()
    {
        var u = new VendorUser();
        for (var i = 0; i < 4; i++) Assert.False(u.RegisterFailure(Now, 5, TimeSpan.FromMinutes(15)));
        Assert.False(u.IsLocked(Now));

        Assert.True(u.RegisterFailure(Now, 5, TimeSpan.FromMinutes(15)));
        Assert.True(u.IsLocked(Now));
        Assert.True(u.IsLocked(Now.AddMinutes(14)));
        Assert.False(u.IsLocked(Now.AddMinutes(16)));
        Assert.Equal(0, u.FailedAttempts);
    }

    [Fact]
    public void A_successful_sign_in_clears_failures_and_locks()
    {
        var u = new VendorUser();
        u.RegisterFailure(Now, 5, TimeSpan.FromMinutes(15));
        u.RegisterSuccess(Now);
        Assert.Equal((0, null), (u.FailedAttempts, u.LockedUntil));
        Assert.Equal(Now, u.LastLoginAt);
    }

    [Fact]
    public void Invites_are_usable_once_and_only_until_they_expire()
    {
        var invite = new VendorInvite { ExpiresAt = Now.AddDays(1) };
        Assert.True(invite.IsUsable(Now));
        Assert.False(invite.IsUsable(Now.AddDays(2)));
        invite.UsedAt = Now;
        Assert.False(invite.IsUsable(Now));
        Assert.False(new VendorInvite { ExpiresAt = Now.AddDays(1), RevokedAt = Now }.IsUsable(Now));
    }
}
