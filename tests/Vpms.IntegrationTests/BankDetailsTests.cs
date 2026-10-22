using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.TestSupport;
using Vpms.Vendors.Application;
using Vpms.Vendors.Contracts;
using Vpms.Vendors.Domain;
using Vpms.Vendors.Infrastructure;
using Vpms.Worker;

namespace Vpms.IntegrationTests;

/// <summary>The acceptance tests for the two-person rule on vendor bank details.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class BankDetailsTests : IAsyncLifetime
{
    private const string Iban1 = "SA0380000000608010167519";
    private const string Iban2 = "AE070331234567890123456";
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private TestDatabase _db = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _maker = Guid.NewGuid();
    private readonly Guid _checker = Guid.NewGuid();
    private Guid _vendor;

    public BankDetailsTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _db = await _sql.CreateDatabaseAsync();
        _host = new IntegrationHost(_db);
        foreach (var t in new[] { _tenantA, _tenantB })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<Vpms.BuildingBlocks.Tenancy.TenantContext>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            db.Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        _vendor = await NewVendor(_tenantA, "Gulf Parts");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // --- the acceptance criterion --------------------------------------------------------------------------

    [Fact]
    public async Task A_proposed_change_has_no_effect_until_a_different_person_approves()
    {
        var change = await Propose(_maker, Iban1);
        Assert.Equal(ChangeStatus.Pending, change.Status);
        Assert.Equal(BankDetailsStatus.Pending, change.Proposed.Status);
        Assert.Null(await Verified());

        var blocked = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_maker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, "CB-1", null)));
        Assert.Equal("maker-checker", blocked.Rule);
        Assert.Null(await Verified());

        var approved = await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, "Checked against the contract"));
        Assert.Equal(ChangeStatus.Approved, approved.Status);
        var verified = await Verified();
        Assert.Equal(("SAR", "••••7519"), (verified!.CurrencyCode, verified.MaskedAccount));
        Assert.Equal(change.Proposed.Id, verified.Id);
    }

    [Fact]
    public async Task The_database_refuses_to_activate_details_without_an_approving_second_person()
    {
        var change = await Propose(_maker, Iban1);

        // 1. Verifying the details directly, with no approved change behind them.
        var direct = await Assert.ThrowsAsync<SqlException>(() => AppSql($"UPDATE [vendor].[BankDetails] SET [Status] = 'Verified' WHERE [Id] = '{change.Proposed.Id}'"));
        Assert.Equal(51010, direct.Number);

        // 2. Approving a change as its own maker.
        var self = await Assert.ThrowsAsync<SqlException>(() => AppSql(
            $"UPDATE [vendor].[BankDetailChange] SET [Status] = 'Approved', [CheckedBy] = [MadeBy], [CheckedAt] = SYSDATETIMEOFFSET() WHERE [Id] = '{change.Id}'"));
        Assert.Equal(547, self.Number); // CHECK constraint

        // 3. Inserting details that are already Verified.
        var insert = await Assert.ThrowsAsync<SqlException>(() => AppSql(
            $"INSERT INTO [vendor].[BankDetails] ([Id],[TenantId],[VendorId],[AccountName],[BankName],[CountryCode],[CurrencyCode],[MaskedAccount],[Status],[CreatedAt]) " +
            $"VALUES (NEWID(), '{_tenantA}', '{_vendor}', 'Evil', 'Evil Bank', 'SA', 'USD', '••••0000', 'Verified', SYSDATETIMEOFFSET())"));
        Assert.Equal(51010, insert.Number);

        // 4. Marking a change approved without a checker at all.
        var noChecker = await Assert.ThrowsAsync<SqlException>(() => AppSql($"UPDATE [vendor].[BankDetailChange] SET [Status] = 'Approved' WHERE [Id] = '{change.Id}'"));
        Assert.Equal(547, noChecker.Number);

        Assert.Null(await Verified());
    }

    [Fact]
    public async Task Decisions_are_immutable_and_evidence_cannot_be_deleted()
    {
        var change = await Propose(_maker, Iban1);
        await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, null));

        var edit = await Assert.ThrowsAsync<SqlException>(() => AppSql($"UPDATE [vendor].[BankDetailChange] SET [CheckedBy] = '{Guid.NewGuid()}' WHERE [Id] = '{change.Id}'"));
        Assert.Equal(51011, edit.Number);
        var delChange = await Assert.ThrowsAsync<SqlException>(() => AppSql($"DELETE FROM [vendor].[BankDetailChange] WHERE [Id] = '{change.Id}'"));
        Assert.Equal(229, delChange.Number);
        var delDetails = await Assert.ThrowsAsync<SqlException>(() => AppSql($"DELETE FROM [vendor].[BankDetails] WHERE [Id] = '{change.Proposed.Id}'"));
        Assert.Equal(229, delDetails.Number);
    }

    // --- lifecycle -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Replacing_active_details_needs_a_callback_and_supersedes_the_old_ones()
    {
        await Activate(Iban1);
        var change = await Propose(_maker, Iban2, country: "AE", currency: "SAR", account: "Gulf Parts LLC");
        Assert.True(change.RequiresCallback);
        Assert.NotNull(change.ReplacesBankDetailsId);

        var missing = await Assert.ThrowsAsync<ValidationFailedException>(() => As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, null)));
        Assert.Equal("callback_required", missing.Code);
        Assert.Equal("••••7519", (await Verified())!.MaskedAccount);

        await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, "CB-0042: called finance contact on file", null));
        Assert.Equal("••••3456", (await Verified())!.MaskedAccount);

        var all = await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ListAsync(_vendor));
        Assert.Equal(["Superseded", "Verified"], all.Select(b => b.Status).Order());
        Assert.Single(all, b => b.Status == BankDetailsStatus.Verified);
    }

    [Fact]
    public async Task A_second_currency_is_independent_of_the_first()
    {
        await Activate(Iban1);
        var usd = await Propose(_maker, "GB82WEST12345698765432", country: "GB", currency: "USD");
        Assert.Null(usd.ReplacesBankDetailsId);
        await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(usd.Id, null, null));
        Assert.NotNull(await Verified("SAR"));
        Assert.NotNull(await Verified("USD"));
    }

    [Fact]
    public async Task Rejected_and_cancelled_changes_never_take_effect()
    {
        var first = await Propose(_maker, Iban1);
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_maker, s => s.GetRequiredService<IBankDetailsService>().RejectAsync(first.Id, "mine")));
        var rejected = await As(_checker, s => s.GetRequiredService<IBankDetailsService>().RejectAsync(first.Id, "IBAN does not match the contract"));
        Assert.Equal((ChangeStatus.Rejected, BankDetailsStatus.Revoked), (rejected.Status, (await Details(first.Proposed.Id)).Status));

        var second = await Propose(_maker, Iban1);
        Assert.Equal("not_the_maker", (await Assert.ThrowsAsync<ConflictException>(() => As(_checker, s => s.GetRequiredService<IBankDetailsService>().CancelAsync(second.Id)))).Code);
        var cancelled = await As(_maker, s => s.GetRequiredService<IBankDetailsService>().CancelAsync(second.Id));
        Assert.Equal(ChangeStatus.Cancelled, cancelled.Status);

        Assert.Null(await Verified());
        Assert.Equal("change_not_pending", (await Assert.ThrowsAsync<ConflictException>(() => As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(second.Id, null, null)))).Code);
    }

    [Fact]
    public async Task Only_one_change_can_be_pending_per_vendor_and_currency_and_no_op_changes_are_refused()
    {
        var first = await Propose(_maker, Iban1);
        Assert.Equal("change_pending", (await Assert.ThrowsAsync<ConflictException>(() => Propose(_maker, Iban2, country: "AE"))).Code);
        await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(first.Id, null, null));
        Assert.Equal("no_change", (await Assert.ThrowsAsync<ConflictException>(() => Propose(_maker, "SA03 8000 0000 6080 1016 7519"))).Code);
    }

    [Fact]
    public async Task Stale_requests_expire_and_can_no_longer_be_approved()
    {
        var change = await Propose(_maker, Iban1);
        await using var host = new IntegrationHost(_db, new LaterClock(TimeSpan.FromDays(15)));
        await using (var scope = host.Scope(_tenantA, _checker))
        {
            var ex = await Assert.ThrowsAsync<ConflictException>(() => scope.ServiceProvider.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, null));
            Assert.Equal("change_expired", ex.Code);
        }

        var job = new BankChangeExpiryJob(host.ScopeFactory, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BankChangeExpiryJob>.Instance);
        Assert.Equal(1, await job.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, await job.RunOnceAsync(CancellationToken.None));

        var after = (await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ListChangesAsync(_vendor, null))).Single();
        Assert.Equal((ChangeStatus.Expired, BankDetailsStatus.Revoked), (after.Status, after.Proposed.Status));
        Assert.Null(await Verified());
    }

    [Fact]
    public async Task Two_checkers_approving_at_the_same_moment_activate_it_exactly_once()
    {
        var change = await Propose(_maker, Iban1);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try
            {
                await As(Guid.NewGuid(), s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, null));
                return true;
            }
            catch (Exception ex) when (ex is ConflictException or DbUpdateException or DbUpdateConcurrencyException)
            {
                return false;
            }
        }));

        Assert.Equal(1, results.Count(r => r));
        var all = await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ListAsync(_vendor));
        Assert.Single(all, b => b.Status == BankDetailsStatus.Verified);
    }

    // --- confidentiality -----------------------------------------------------------------------------------

    [Fact]
    public async Task Account_numbers_are_encrypted_masked_in_lists_and_every_reveal_is_audited()
    {
        var change = await Propose(_maker, Iban1, account: "123456789012");
        var json = System.Text.Json.JsonSerializer.Serialize(change);
        Assert.DoesNotContain(Iban1, json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789012", json, StringComparison.Ordinal);

        await using (var scope = _host.Scope(_tenantA))
        {
            var row = await scope.ServiceProvider.GetRequiredService<VendorsDbContext>().BankDetails.AsNoTracking().SingleAsync();
            Assert.StartsWith("v1.", row.IbanCipher);
            Assert.StartsWith("v1.", row.AccountNumberCipher);
            Assert.DoesNotContain("SA03", row.IbanCipher, StringComparison.Ordinal);
        }

        var revealed = await As(_checker, s => s.GetRequiredService<IBankDetailsService>().RevealAsync(change.Proposed.Id));
        Assert.Equal((Iban1, "123456789012"), (revealed.Iban, revealed.AccountNumber));

        await using var read = _host.Scope(_tenantA);
        var events = (await read.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Limit: 100), null)).Items;
        var reveal = Assert.Single(events, e => e.Action == "bank-details.revealed");
        Assert.Equal(_checker, reveal.ActorId);
        Assert.DoesNotContain(events, e => (e.ChangeSummaryJson ?? "").Contains(Iban1, StringComparison.Ordinal) || (e.ChangeSummaryJson ?? "").Contains("123456789012", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_tenants_cannot_list_decide_or_reveal()
    {
        var change = await Propose(_maker, Iban1);
        await using var scope = _host.Scope(_tenantB, _checker);
        var svc = scope.ServiceProvider.GetRequiredService<IBankDetailsService>();
        Assert.Empty(await svc.ListAsync(_vendor));
        Assert.Empty(await svc.ListChangesAsync(null, null));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.ApproveAsync(change.Id, null, null));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.RevealAsync(change.Proposed.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.ProposeAsync(_vendor, Request(Iban1)));
    }

    [Fact]
    public async Task An_account_already_used_by_another_vendor_is_flagged_as_possible_fraud()
    {
        await Activate(Iban1);
        var other = await NewVendor(_tenantA, "Second Vendor");
        var ex = await Assert.ThrowsAsync<ConflictException>(() => As(_maker, s => s.GetRequiredService<IBankDetailsService>().ProposeAsync(other, Request(Iban1))));
        Assert.Equal("bank_account_shared", ex.Code);
        var confirmed = await As(_maker, s => s.GetRequiredService<IBankDetailsService>().ProposeAsync(other, Request(Iban1) with { ConfirmSharedAccount = true }));
        Assert.Equal(ChangeStatus.Pending, confirmed.Status);
    }

    [Theory]
    [InlineData("SA0380000000608010167518", "SA", "invalid_iban")]
    [InlineData(null, "SA", "iban_required")]
    [InlineData(Iban1, "AE", "iban_country_mismatch")]
    public async Task Invalid_account_data_is_rejected(string? iban, string country, string expected)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => As(_maker, s => s.GetRequiredService<IBankDetailsService>()
            .ProposeAsync(_vendor, Request(iban) with { CountryCode = country })));
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task Payments_can_see_verified_details_and_whether_a_change_is_in_flight()
    {
        Assert.False(await As(_maker, s => s.GetRequiredService<IBankDetailsLookup>().HasPendingChangeAsync(_vendor)));
        var change = await Propose(_maker, Iban1);
        Assert.True(await As(_maker, s => s.GetRequiredService<IBankDetailsLookup>().HasPendingChangeAsync(_vendor)));
        await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, null));
        Assert.False(await As(_maker, s => s.GetRequiredService<IBankDetailsLookup>().HasPendingChangeAsync(_vendor)));

        var before = await Verified();
        var replace = await Propose(_maker, Iban2, country: "AE");
        await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(replace.Id, "CB-7", null));
        Assert.NotEqual(before!.Fingerprint, (await Verified())!.Fingerprint); // a changed account changes the fingerprint
    }

    [Fact]
    public async Task The_audit_trail_records_who_proposed_blocked_and_approved()
    {
        var change = await Propose(_maker, Iban1);
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_maker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, null)));
        await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, null));

        await using var scope = _host.Scope(_tenantA);
        var events = (await scope.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Limit: 200), null)).Items;
        string[] expected = ["bank-details.change.proposed", "sod.bank-details.blocked", "bank-details.change.approved", "bank-details.activated"];
        Assert.Equal(expected, events.Where(e => expected.Contains(e.Action)).Select(e => e.Action));
        Assert.Equal(_maker, events.First(e => e.Action == "bank-details.change.proposed").ActorId);
        Assert.Equal(_checker, events.First(e => e.Action == "bank-details.change.approved").ActorId);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(_tenantA)).IsValid);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private static ProposeBankDetails Request(string? iban, string country = "SA", string currency = "SAR", string? account = null) =>
        new("Gulf Parts LLC", "National Bank", "NCBKSAJE", country, currency, iban, account, "New supplier onboarding");

    private Task<BankDetailChangeDto> Propose(Guid actor, string iban, string country = "SA", string currency = "SAR", string? account = null) =>
        As(actor, s => s.GetRequiredService<IBankDetailsService>().ProposeAsync(_vendor, Request(iban, country, currency, account)));

    private async Task Activate(string iban)
    {
        var change = await Propose(_maker, iban);
        await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ApproveAsync(change.Id, null, null));
    }

    private Task<VerifiedBankDetails?> Verified(string currency = "SAR") =>
        As(_maker, s => s.GetRequiredService<IBankDetailsLookup>().GetVerifiedAsync(_vendor, currency));

    private async Task<BankDetailsDto> Details(Guid id) =>
        (await As(_checker, s => s.GetRequiredService<IBankDetailsService>().ListAsync(_vendor))).Single(b => b.Id == id);

    private async Task<T> As<T>(Guid actor, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _host.Scope(_tenantA, actor);
        return await action(scope.ServiceProvider);
    }

    private async Task<Guid> NewVendor(Guid tenant, string name)
    {
        await using var scope = _host.Scope(tenant, _maker);
        return (await scope.ServiceProvider.GetRequiredService<IVendorService>().CreateAsync(new CreateVendor(name, null, null, "SA", null, null, null, null))).Id;
    }

    /// <summary>Runs SQL as the restricted application login, in the vendor's tenant.</summary>
    private async Task AppSql(string sql)
    {
        await using var conn = new SqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();
        await using (var ctx = conn.CreateCommand())
        {
            ctx.CommandText = $"EXEC sp_set_session_context N'TenantId', '{_tenantA}'";
            await ctx.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class LaterClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
