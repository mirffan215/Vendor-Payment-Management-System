using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Errors;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Documents.Application;
using Vpms.Documents.Contracts;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class CustomerAdminTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private TestDatabase _db = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    public CustomerAdminTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _db = await _sql.CreateDatabaseAsync();
        _host = new IntegrationHost(_db);
        foreach (var (id, slug) in new[] { (_tenantA, "a"), (_tenantB, "b") })
        {
            await using var scope = _host.Scope(null);
            var tenant = scope.ServiceProvider.GetRequiredService<Vpms.BuildingBlocks.Tenancy.TenantContext>();
            using var _ = tenant.BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            db.Tenants.Add(new Tenant { Id = id, Name = "Tenant " + slug, Slug = slug, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // --- legal entities ------------------------------------------------------------------------------------

    [Fact]
    public async Task Legal_entities_are_created_with_an_encrypted_masked_tax_number()
    {
        var created = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(
            new CreateLegalEntity("sa-01", "Acme Saudi Arabia LLC", "أكمي السعودية", "SA", "SAR", "3000 1234-5600003")));

        Assert.Equal("SA-01", created.Code);
        Assert.True(created.HasTaxRegistrationNo);
        Assert.Equal("••••003", created.TaxRegistrationNoMasked);
        Assert.Equal("أكمي السعودية", created.LegalNameAr);

        await using var read = _host.Scope(_tenantA);
        var row = await read.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().LegalEntities.AsNoTracking().SingleAsync();
        Assert.StartsWith("v1.", row.TaxRegistrationNoCipher);
        Assert.DoesNotContain("30001234", row.TaxRegistrationNoCipher, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", row.TaxRegistrationNoIndex);
    }

    [Fact]
    public async Task Duplicate_codes_and_tax_numbers_are_conflicts_within_a_tenant_but_not_across_tenants()
    {
        await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E1", "One", null, "SA", "SAR", "300012345600003")));

        var code = await Assert.ThrowsAsync<ConflictException>(() => Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>()
            .CreateAsync(new CreateLegalEntity("e1", "Other", null, "SA", "SAR", null))));
        Assert.Equal("entity_code_exists", code.Code);

        var tax = await Assert.ThrowsAsync<ConflictException>(() => Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>()
            .CreateAsync(new CreateLegalEntity("E2", "Two", null, "SA", "SAR", "3000-1234-5600003"))));
        Assert.Equal("tax_number_exists", tax.Code);

        // Another customer may of course register the same entity code and tax number.
        await Run(_tenantB, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E1", "One", null, "SA", "SAR", "300012345600003")));
    }

    [Theory]
    [InlineData("", "Name", "SA", "SAR", "invalid_code")]
    [InlineData("bad code!", "Name", "SA", "SAR", "invalid_code")]
    [InlineData("E1", "", "SA", "SAR", "invalid_name")]
    [InlineData("E1", "Name", "SAU", "SAR", "invalid_country")]
    [InlineData("E1", "Name", "sa", "SAR", "invalid_country")]
    [InlineData("E1", "Name", "SA", "SR", "invalid_currency")]
    public async Task Invalid_entities_are_rejected(string code, string name, string country, string currency, string expected)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>()
            .CreateAsync(new CreateLegalEntity(code, name, null, country, currency, null))));
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task Entities_are_listed_per_scope_and_never_across_tenants()
    {
        var one = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E1", "One", null, "SA", "SAR", null)));
        var two = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E2", "Two", null, "AE", "AED", null)));
        await Run(_tenantB, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("B1", "Beta", null, "SA", "SAR", null)));

        Assert.Equal(["E1", "E2"], (await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().ListAsync(null))).Select(e => e.Code));
        Assert.Equal(["E2"], (await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().ListAsync(new HashSet<Guid> { two.Id }))).Select(e => e.Code));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(_tenantB, s => s.GetRequiredService<ILegalEntityService>().GetAsync(one.Id)));
    }

    [Fact]
    public async Task Updates_and_status_changes_are_audited_without_the_tax_number()
    {
        var e = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E1", "One", null, "SA", "SAR", "300012345600003")));
        await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().UpdateAsync(e.Id, new UpdateLegalEntity("One Renamed", "الأولى", "300099999900003")));
        var inactive = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().SetStatusAsync(e.Id, false));
        Assert.Equal("Inactive", inactive.Status);

        await using var scope = _host.Scope(_tenantA);
        var events = (await scope.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(LegalEntityId: e.Id, Limit: 50), null)).Items;
        Assert.Equal(["legal-entity.created", "legal-entity.updated", "legal-entity.status-changed"], events.Select(x => x.Action));
        Assert.All(events, x => Assert.Equal(_admin, x.ActorId));
        Assert.DoesNotContain(events, x => (x.ChangeSummaryJson ?? "").Contains("3000", StringComparison.Ordinal));
    }

    // --- tenant profile ------------------------------------------------------------------------------------

    [Fact]
    public async Task Tenant_profile_can_be_updated_and_validated()
    {
        var updated = await Run(_tenantA, s => s.GetRequiredService<ITenantProfileService>()
            .UpdateAsync(new UpdateTenantProfile("Acme Group", "ar", "hijri", "#0B5CAD", null)));
        Assert.Equal(("Acme Group", "ar", "hijri", "#0B5CAD"), (updated.Name, updated.DefaultLocale, updated.DefaultCalendar, updated.PrimaryColor));

        foreach (var (bad, code) in new[]
                 {
                     (new UpdateTenantProfile("", "en", "gregorian", null, null), "invalid_name"),
                     (new UpdateTenantProfile("X", "fr", "gregorian", null, null), "invalid_locale"),
                     (new UpdateTenantProfile("X", "en", "julian", null, null), "invalid_calendar"),
                     (new UpdateTenantProfile("X", "en", "gregorian", "red", null), "invalid_color"),
                 })
        {
            var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, s => s.GetRequiredService<ITenantProfileService>().UpdateAsync(bad)));
            Assert.Equal(code, ex.Code);
        }
    }

    [Fact]
    public async Task The_logo_must_be_a_clean_image_uploaded_as_the_tenants_own_branding()
    {
        AttachmentInfo Upload(IServiceProvider s, string owner) => s.GetRequiredService<IAttachmentService>()
            .UploadAsync(new UploadRequest(owner, _tenantA.ToString(), null, "logo.png", new MemoryStream(Png), _admin, UploaderKind.Staff)).GetAwaiter().GetResult();

        var wrongOwner = await Run(_tenantA, s => Task.FromResult(Upload(s, "Vendor")));
        var right = await Run(_tenantA, s => Task.FromResult(Upload(s, "Tenant")));

        var notClean = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, s => s.GetRequiredService<ITenantProfileService>()
            .UpdateAsync(new UpdateTenantProfile("A", "en", "gregorian", null, right.Id))));
        Assert.Equal("invalid_logo", notClean.Code);

        await ScanAll();
        await Run(_tenantA, s => s.GetRequiredService<ITenantProfileService>().UpdateAsync(new UpdateTenantProfile("A", "en", "gregorian", null, right.Id)));

        var wrong = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, s => s.GetRequiredService<ITenantProfileService>()
            .UpdateAsync(new UpdateTenantProfile("A", "en", "gregorian", null, wrongOwner.Id))));
        Assert.Equal("invalid_logo", wrong.Code);
    }

    // --- approval data -------------------------------------------------------------------------------------

    [Fact]
    public async Task Approval_authorities_validate_overlap_and_can_be_revoked()
    {
        var entity = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E1", "One", null, "SA", "SAR", null)));
        var user = await SeedUser(_tenantA);
        var from = DateTimeOffset.UtcNow.AddDays(-1);

        var a = await Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>().CreateAuthorityAsync(
            new CreateApprovalAuthority(entity.Id, user, 50_000m, "SAR", "{\"costCenter\":[\"100\"]}", from, null)));
        Assert.Equal("Active", a.Status);

        var overlap = await Assert.ThrowsAsync<ConflictException>(() => Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>().CreateAuthorityAsync(
            new CreateApprovalAuthority(entity.Id, user, 10m, "SAR", null, from, null))));
        Assert.Equal("authority_overlaps", overlap.Code);

        // a different currency is a separate authority
        await Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>().CreateAuthorityAsync(new CreateApprovalAuthority(entity.Id, user, 10m, "USD", null, from, null)));

        await RunVoid(_tenantA, s => s.GetRequiredService<IApprovalAdminService>().RevokeAuthorityAsync(a.Id));
        await Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>().CreateAuthorityAsync(new CreateApprovalAuthority(entity.Id, user, 70_000m, "SAR", null, DateTimeOffset.UtcNow, null)));
    }

    [Theory]
    [InlineData(0, "SAR", "invalid_amount")]
    [InlineData(-5, "SAR", "invalid_amount")]
    [InlineData(10, "sar", "invalid_currency")]
    public async Task Invalid_authorities_are_rejected(decimal amount, string currency, string expected)
    {
        var entity = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E1", "One", null, "SA", "SAR", null)));
        var user = await SeedUser(_tenantA);
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>()
            .CreateAuthorityAsync(new CreateApprovalAuthority(entity.Id, user, amount, currency, null, DateTimeOffset.UtcNow, null))));
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task Authorities_need_a_real_user_and_entity_of_the_same_tenant()
    {
        var entityA = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E1", "One", null, "SA", "SAR", null)));
        var userB = await SeedUser(_tenantB);
        var userA = await SeedUser(_tenantA);

        var wrongUser = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>()
            .CreateAuthorityAsync(new CreateApprovalAuthority(entityA.Id, userB, 10, "SAR", null, DateTimeOffset.UtcNow, null))));
        Assert.Equal("unknown_user", wrongUser.Code);

        var wrongEntity = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantB, s => s.GetRequiredService<IApprovalAdminService>()
            .CreateAuthorityAsync(new CreateApprovalAuthority(entityA.Id, userB, 10, "SAR", null, DateTimeOffset.UtcNow, null))));
        Assert.Equal("unknown_legal_entity", wrongEntity.Code);

        var badPeriod = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>()
            .CreateAuthorityAsync(new CreateApprovalAuthority(entityA.Id, userA, 10, "SAR", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1)))));
        Assert.Equal("invalid_period", badPeriod.Code);
    }

    [Fact]
    public async Task Approval_rules_need_valid_json_objects()
    {
        var entity = await Run(_tenantA, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity("E1", "One", null, "SA", "SAR", null)));
        var rule = await Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>().CreateRuleAsync(
            new CreateApprovalMatrixRule(entity.Id, "Over 100k", 10, "{\"amountFrom\":100000}", "{\"role\":\"Invoice Approver\"}", 2)));
        Assert.Equal("Active", rule.Status);

        foreach (var (conditions, resolution) in new[] { ("not json", "{}"), ("[]", "{}"), ("{}", "\"text\""), (new string('x', 9000), "{}") })
        {
            var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>()
                .CreateRuleAsync(new CreateApprovalMatrixRule(entity.Id, "Bad", 1, conditions, resolution, 1))));
            Assert.Equal("invalid_json", ex.Code);
        }

        await RunVoid(_tenantA, s => s.GetRequiredService<IApprovalAdminService>().DisableRuleAsync(rule.Id));
        Assert.Equal("Disabled", (await Run(_tenantA, s => s.GetRequiredService<IApprovalAdminService>().ListRulesAsync(entity.Id, null))).Single().Status);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private async Task<T> Run<T>(Guid tenant, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _host.Scope(tenant, _admin);
        return await action(scope.ServiceProvider);
    }

    private async Task RunVoid(Guid tenant, Func<IServiceProvider, Task> action)
    {
        await using var scope = _host.Scope(tenant, _admin);
        await action(scope.ServiceProvider);
    }

    private async Task<Guid> SeedUser(Guid tenant)
    {
        var id = Guid.NewGuid();
        await using var scope = _host.Scope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<Vpms.Identity.Infrastructure.IdentityDbContext>();
        db.Users.Add(new Vpms.Identity.Domain.User { Id = id, ProviderId = Guid.NewGuid(), ExternalSubject = "s" + id, Email = id + "@x.com", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task ScanAll()
    {
        IReadOnlyList<PendingScan> pending;
        await using (var list = _host.Scope(null))
        {
            pending = await list.ServiceProvider.GetRequiredService<IAttachmentScanService>().ListPendingAsync(100);
        }

        foreach (var item in pending)
        {
            await using var scope = _host.Scope(item.TenantId);
            await scope.ServiceProvider.GetRequiredService<IAttachmentScanService>().ScanAsync(item.AttachmentId);
        }
    }
}
