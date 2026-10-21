using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.TestSupport;
using Vpms.VendorPortal.Application;
using Vpms.VendorPortal.Domain;
using Vpms.VendorPortal.Infrastructure;
using Vpms.Vendors.Application;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PortalAuthTests : IAsyncLifetime
{
    private const string Good = "purple elephants dance quietly";
    private readonly SqlServerFixture _sql;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _staff1 = Guid.NewGuid();
    private readonly Guid _staff2 = Guid.NewGuid();
    private Guid _vendor;

    public PortalAuthTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(await _sql.CreateDatabaseAsync(), _clock);
        foreach (var (id, slug) in new[] { (_tenantA, "alpha"), (_tenantB, "beta") })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<Vpms.BuildingBlocks.Tenancy.TenantContext>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            db.Tenants.Add(new Tenant { Id = id, Name = slug, Slug = slug, CreatedAt = _clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        _vendor = await Staff(_tenantA, _staff1, s => s.GetRequiredService<IVendorService>()
            .CreateAsync(new CreateVendor("Gulf Parts", null, null, "SA", null, null, null, null))).ContinueWith(t => t.Result.Id);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // --- the happy path ------------------------------------------------------------------------------------

    [Fact]
    public async Task Invitation_to_signed_in_session_with_password_and_authenticator()
    {
        var (email, token, _) = await Invite("ap@gulfparts.example");

        var info = await Anon(s => s.GetInviteAsync("alpha", token));
        Assert.Equal(("ap@gulfparts.example", "Gulf Parts", true, "Invite"), (info.Email, info.VendorName, info.NeedsAuthenticator, info.Purpose));

        // A weak password is refused with a reason that does not echo it.
        var weak = await Assert.ThrowsAsync<ValidationFailedException>(() => Anon(s => s.AcceptInviteAsync("alpha", token, "short")));
        Assert.Equal("weak_password_too_short", weak.Code);
        Assert.Equal("weak_password_contains_personal_data", (await Assert.ThrowsAsync<ValidationFailedException>(() => Anon(s => s.AcceptInviteAsync("alpha", token, "my ap@gulfparts passphrase")))).Code);

        var accepted = await Anon(s => s.AcceptInviteAsync("alpha", token, Good));
        Assert.False(accepted.Completed);
        var enrollment = accepted.Enrollment!;
        Assert.StartsWith("otpauth://totp/VPMS:", enrollment.ProvisioningUri);

        // Not signed in yet: no session without the authenticator.
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.LoginAsync("alpha", email, Good)));

        var wrong = await Assert.ThrowsAsync<ValidationFailedException>(() => Anon(s => s.CompleteEnrollmentAsync("alpha", token, "000000")));
        Assert.Equal("invalid_code", wrong.Code);

        var done = await Anon(s => s.CompleteEnrollmentAsync("alpha", token, CodeFor(enrollment.Secret)));
        Assert.Equal((_vendor, _tenantA, email), (done.Session.VendorId, done.Session.TenantId, done.Session.Email));
        Assert.Equal(10, done.RecoveryCodes.Count);
        Assert.Equal(10, done.RecoveryCodes.Distinct().Count());
        Assert.All(done.RecoveryCodes, c => Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", c));

        // The invitation is spent.
        await Assert.ThrowsAsync<NotFoundException>(() => Anon(s => s.GetInviteAsync("alpha", token)));

        // Sign in: password, then a fresh authenticator code.
        _clock.Advance(TimeSpan.FromSeconds(31));
        var challenge = await Anon(s => s.LoginAsync("alpha", email.ToUpperInvariant(), Good));
        var session = await Anon(s => s.VerifyMfaAsync(challenge.Challenge, CodeFor(enrollment.Secret)));
        Assert.Equal(done.Session.UserId, session.UserId);
    }

    [Fact]
    public async Task An_authenticator_code_works_only_once()
    {
        var (email, secret) = await Enrolled("once@gulfparts.example");
        _clock.Advance(TimeSpan.FromSeconds(31));

        var code = CodeFor(secret);
        var c1 = await Anon(s => s.LoginAsync("alpha", email, Good));
        await Anon(s => s.VerifyMfaAsync(c1.Challenge, code));

        var c2 = await Anon(s => s.LoginAsync("alpha", email, Good));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.VerifyMfaAsync(c2.Challenge, code))); // replay

        _clock.Advance(TimeSpan.FromSeconds(31));
        await Anon(s => s.VerifyMfaAsync(c2.Challenge, CodeFor(secret)));
    }

    [Fact]
    public async Task Recovery_codes_work_once_each()
    {
        var (email, secret, codes) = await EnrolledWithCodes("recover@gulfparts.example");
        var challenge = await Anon(s => s.LoginAsync("alpha", email, Good));
        await Anon(s => s.VerifyMfaAsync(challenge.Challenge, codes[0].ToLowerInvariant().Replace("-", " ")));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.VerifyMfaAsync(challenge.Challenge, codes[0])));
        await Anon(s => s.VerifyMfaAsync(challenge.Challenge, codes[1]));
        Assert.NotNull(secret);
    }

    // --- uniform failures and lockout ----------------------------------------------------------------------

    [Fact]
    public async Task Every_kind_of_failed_sign_in_looks_the_same()
    {
        var (email, _) = await Enrolled("uniform@gulfparts.example");
        var attempts = new Func<Task>[]
        {
            () => Anon(s => s.LoginAsync("alpha", "nobody@gulfparts.example", Good)),        // unknown user
            () => Anon(s => s.LoginAsync("alpha", email, "wrong password entirely")),        // wrong password
            () => Anon(s => s.LoginAsync("no-such-tenant", email, Good)),                    // unknown tenant
            () => Anon(s => s.LoginAsync("beta", email, Good)),                              // right credentials, wrong tenant
            () => Anon(s => s.LoginAsync("alpha", "", "")),                                  // empty
        };
        var messages = new List<string>();
        foreach (var attempt in attempts)
        {
            var ex = await Assert.ThrowsAsync<InvalidCredentialsException>(attempt);
            messages.Add(ex.Message);
        }

        Assert.Single(messages.Distinct());
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_against_the_right_password_until_the_lockout_ends()
    {
        var (email, secret) = await Enrolled("lock@gulfparts.example");
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.LoginAsync("alpha", email, "wrong password " + i)));

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.LoginAsync("alpha", email, Good))); // correct, but locked

        _clock.Advance(TimeSpan.FromMinutes(16));
        var challenge = await Anon(s => s.LoginAsync("alpha", email, Good));
        await Anon(s => s.VerifyMfaAsync(challenge.Challenge, CodeFor(secret)));

        var events = await Events(_tenantA);
        Assert.Contains("portal.user.locked", events);
        Assert.Contains("portal.login.rejected", events);
    }

    [Fact]
    public async Task Wrong_authenticator_codes_count_towards_the_lockout_too()
    {
        var (email, secret) = await Enrolled("mfalock@gulfparts.example");
        var challenge = await Anon(s => s.LoginAsync("alpha", email, Good));
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.VerifyMfaAsync(challenge.Challenge, "00000" + i)));

        // The attacker held the password but cannot keep guessing codes: the account is locked.
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.VerifyMfaAsync(challenge.Challenge, CodeFor(secret))));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.LoginAsync("alpha", email, Good)));
    }

    [Fact]
    public async Task Challenges_expire_and_cannot_be_forged()
    {
        var (email, secret) = await Enrolled("challenge@gulfparts.example");
        var challenge = await Anon(s => s.LoginAsync("alpha", email, Good));

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.VerifyMfaAsync(challenge.Challenge + "x", CodeFor(secret))));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.VerifyMfaAsync("", CodeFor(secret))));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.VerifyMfaAsync("{\"UserId\":\"" + Guid.NewGuid() + "\"}", CodeFor(secret))));

        _clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.VerifyMfaAsync(challenge.Challenge, CodeFor(secret))));
    }

    // --- invitations ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Invitations_expire_are_replaced_by_new_ones_and_belong_to_one_tenant()
    {
        var (_, token, userId) = await Invite("expiry@gulfparts.example");
        await Assert.ThrowsAsync<NotFoundException>(() => Anon(s => s.GetInviteAsync("beta", token)));          // another tenant's slug
        await Assert.ThrowsAsync<NotFoundException>(() => Anon(s => s.GetInviteAsync("alpha", token + "x")));   // altered token
        await Assert.ThrowsAsync<NotFoundException>(() => Anon(s => s.GetInviteAsync("alpha", "")));

        var replacement = await Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAdminService>().ReissueInviteAsync(userId));
        await Assert.ThrowsAsync<NotFoundException>(() => Anon(s => s.GetInviteAsync("alpha", token)));          // revoked by the new one
        var newToken = TokenFrom(replacement.InviteUrl);
        await Anon(s => s.GetInviteAsync("alpha", newToken));

        _clock.Advance(TimeSpan.FromDays(8));
        await Assert.ThrowsAsync<NotFoundException>(() => Anon(s => s.GetInviteAsync("alpha", newToken)));
    }

    [Fact]
    public async Task Only_the_hash_of_the_invitation_is_stored_and_secrets_are_protected_at_rest()
    {
        var (email, token, userId) = await Invite("rest@gulfparts.example");
        var accepted = await Anon(s => s.AcceptInviteAsync("alpha", token, Good));
        var done = await Anon(s => s.CompleteEnrollmentAsync("alpha", token, CodeFor(accepted.Enrollment!.Secret)));

        await using var scope = _host.Scope(_tenantA);
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.StartsWith("AQAAAA", user.PasswordHash);                       // ASP.NET Identity v3 PBKDF2 format
        Assert.DoesNotContain(Good, user.PasswordHash, StringComparison.Ordinal);
        Assert.StartsWith("v1.", user.TotpSecretCipher);
        Assert.DoesNotContain(accepted.Enrollment!.Secret, user.TotpSecretCipher, StringComparison.Ordinal);

        var invite = await db.Invites.AsNoTracking().SingleAsync(i => i.VendorUserId == userId);
        Assert.Equal(64, invite.TokenHash.Length);
        Assert.NotEqual(token, invite.TokenHash);

        var stored = await db.RecoveryCodes.AsNoTracking().Where(c => c.VendorUserId == userId).Select(c => c.CodeHash).ToListAsync();
        Assert.Equal(10, stored.Count);
        Assert.All(done.RecoveryCodes, c => Assert.DoesNotContain(stored, h => h.Contains(c.Replace("-", ""), StringComparison.Ordinal)));
        Assert.Equal(email, user.Email);
    }

    [Fact]
    public async Task Forgotten_passwords_are_reset_through_a_new_link_and_keep_the_authenticator()
    {
        var (email, secret, userId) = await EnrolledFull("forgot@gulfparts.example");
        var reissue = await Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAdminService>().ReissueInviteAsync(userId));
        var token = TokenFrom(reissue.InviteUrl);
        var info = await Anon(s => s.GetInviteAsync("alpha", token));
        Assert.Equal(("PasswordReset", false), (info.Purpose, info.NeedsAuthenticator));

        var result = await Anon(s => s.AcceptInviteAsync("alpha", token, "a brand new quiet passphrase"));
        Assert.True(result.Completed);
        Assert.Null(result.Enrollment);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.LoginAsync("alpha", email, Good)));
        _clock.Advance(TimeSpan.FromSeconds(31));
        var challenge = await Anon(s => s.LoginAsync("alpha", email, "a brand new quiet passphrase"));
        await Anon(s => s.VerifyMfaAsync(challenge.Challenge, CodeFor(secret))); // same authenticator as before
    }

    // --- staff controls ------------------------------------------------------------------------------------

    [Fact]
    public async Task Resetting_an_authenticator_needs_a_second_staff_member_and_restarts_setup()
    {
        var (email, secret, userId) = await EnrolledFull("lostphone@gulfparts.example");
        await Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAdminService>().RequestMfaResetAsync(userId));

        var self = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAdminService>().ApproveMfaResetAsync(userId)));
        Assert.Equal("maker-checker", self.Rule);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var stillWorks = await Anon(s => s.LoginAsync("alpha", email, Good)); // nothing changed yet
        Assert.NotNull(stillWorks);

        var invite = await Staff(_tenantA, _staff2, s => s.GetRequiredService<IPortalAdminService>().ApproveMfaResetAsync(userId));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.LoginAsync("alpha", email, Good)));  // password cleared too
        var info = await Anon(s => s.GetInviteAsync("alpha", TokenFrom(invite.InviteUrl)));
        Assert.True(info.NeedsAuthenticator);

        await using var scope = _host.Scope(_tenantA);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<PortalDbContext>().RecoveryCodes.Where(c => c.VendorUserId == userId).ToListAsync());
        Assert.NotNull(secret);
    }

    [Fact]
    public async Task Disabled_users_cannot_sign_in_or_keep_a_session_and_invitations_are_unique_per_email()
    {
        var (email, _, userId) = await EnrolledFull("disabled@gulfparts.example");
        Assert.True(await SessionValid(userId));
        await Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAdminService>().SetStatusAsync(userId, false));
        Assert.False(await SessionValid(userId));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.LoginAsync("alpha", email, Good)));

        Assert.Equal("portal_user_exists", (await Assert.ThrowsAsync<ConflictException>(() => Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAdminService>()
            .InviteUserAsync(_vendor, email, "Again")))).Code);
        Assert.Equal("invalid_email", (await Assert.ThrowsAsync<ValidationFailedException>(() => Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAdminService>()
            .InviteUserAsync(_vendor, "not-an-email", "X")))).Code);
    }

    [Fact]
    public async Task Other_tenants_staff_cannot_manage_these_users()
    {
        var (_, _, userId) = await EnrolledFull("tenantiso@gulfparts.example");
        await Assert.ThrowsAsync<NotFoundException>(() => Staff(_tenantB, _staff1, s => s.GetRequiredService<IPortalAdminService>().ReissueInviteAsync(userId)));
        await Assert.ThrowsAsync<NotFoundException>(() => Staff(_tenantB, _staff1, s => s.GetRequiredService<IPortalAdminService>().SetStatusAsync(userId, false)));
        Assert.Empty(await Staff(_tenantB, _staff1, s => s.GetRequiredService<IPortalAdminService>().ListUsersAsync(_vendor)));
    }

    [Fact]
    public async Task The_audit_trail_covers_the_flow_and_never_contains_secrets()
    {
        var (email, token, _) = await Invite("audit@gulfparts.example");
        var accepted = await Anon(s => s.AcceptInviteAsync("alpha", token, Good));
        var done = await Anon(s => s.CompleteEnrollmentAsync("alpha", token, CodeFor(accepted.Enrollment!.Secret)));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Anon(s => s.LoginAsync("alpha", email, "wrong wrong wrong wrong")));

        await using var scope = _host.Scope(_tenantA);
        var items = (await scope.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Limit: 200), null)).Items;
        var actions = items.Select(e => e.Action).ToList();
        foreach (var expected in new[] { "portal.user.created", "portal.invite.issued", "portal.password.set", "portal.mfa.enrolled", "portal.login.succeeded", "portal.login.failed" })
        {
            Assert.Contains(expected, actions);
        }

        var all = string.Join('\n', items.Select(e => e.ChangeSummaryJson));
        Assert.DoesNotContain(Good, all, StringComparison.Ordinal);
        Assert.DoesNotContain(token, all, StringComparison.Ordinal);
        Assert.DoesNotContain(accepted.Enrollment!.Secret, all, StringComparison.Ordinal);
        Assert.All(done.RecoveryCodes, c => Assert.DoesNotContain(c, all, StringComparison.Ordinal));
        Assert.DoesNotContain(email, all, StringComparison.Ordinal);

        await using var verify = _host.Scope(null);
        Assert.True((await verify.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(_tenantA)).IsValid);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private string CodeFor(string secret) => Totp.Compute(secret, Totp.StepFor(_clock.GetUtcNow()));

    private static string TokenFrom(string url) => url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];

    private async Task<T> Staff<T>(Guid tenant, Guid actor, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _host.Scope(tenant, actor);
        return await action(scope.ServiceProvider);
    }

    /// <summary>Session checks run after the host has established the tenant from the session cookie.</summary>
    private Task<bool> SessionValid(Guid userId) => Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAuthService>().IsSessionValidAsync(userId));

    private async Task<T> Anon<T>(Func<IPortalAuthService, Task<T>> action)
    {
        await using var scope = _host.Scope(null);
        return await action(scope.ServiceProvider.GetRequiredService<IPortalAuthService>());
    }

    private async Task<(string Email, string Token, Guid UserId)> Invite(string email)
    {
        var result = await Staff(_tenantA, _staff1, s => s.GetRequiredService<IPortalAdminService>().InviteUserAsync(_vendor, email, "Gulf Parts"));
        return (email, TokenFrom(result.InviteUrl), result.User.Id);
    }

    private sealed record Enr(string Email, string Secret, IReadOnlyList<string> Codes, Guid UserId);

    private async Task<Enr> EnrollAsync(string email)
    {
        var (_, token, id) = await Invite(email);
        var accepted = await Anon(s => s.AcceptInviteAsync("alpha", token, Good));
        var done = await Anon(s => s.CompleteEnrollmentAsync("alpha", token, CodeFor(accepted.Enrollment!.Secret)));
        _clock.Advance(TimeSpan.FromSeconds(31)); // the enrolment code is spent; the next sign-in needs a newer one
        return new Enr(email, accepted.Enrollment!.Secret, done.RecoveryCodes, id);
    }

    private async Task<(string Email, string Secret, Guid UserId)> EnrolledFull(string email)
    {
        var e = await EnrollAsync(email);
        return (e.Email, e.Secret, e.UserId);
    }

    private async Task<(string Email, string Secret)> Enrolled(string email)
    {
        var e = await EnrollAsync(email);
        return (e.Email, e.Secret);
    }

    private async Task<(string Email, string Secret, IReadOnlyList<string> Codes)> EnrolledWithCodes(string email)
    {
        var e = await EnrollAsync(email);
        return (e.Email, e.Secret, e.Codes);
    }

    private async Task<List<string>> Events(Guid tenant)
    {
        await using var scope = _host.Scope(tenant);
        return (await scope.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Limit: 500), null)).Items.Select(e => e.Action).ToList();
    }
}
