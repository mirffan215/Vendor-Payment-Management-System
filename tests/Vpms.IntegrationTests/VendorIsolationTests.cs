using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.TestSupport;
using Vpms.Vendors.Application;
using Vpms.Vendors.Domain;
using Vpms.Vendors.Infrastructure;

namespace Vpms.IntegrationTests;

/// <summary>The data layer keeps a vendor session to its own vendor's rows, on top of the tenant filter.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class VendorIsolationTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _staff = Guid.NewGuid();
    private Guid _v1, _v2, _vB;

    public VendorIsolationTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(await _sql.CreateDatabaseAsync());
        foreach (var t in new[] { _tenantA, _tenantB })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().SaveChangesAsync();
        }

        _v1 = await Seed(_tenantA, "Vendor One");
        _v2 = await Seed(_tenantA, "Vendor Two");
        _vB = await Seed(_tenantB, "Vendor Beta");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_vendor_scope_sees_only_its_own_rows_in_every_vendor_owned_table()
    {
        await using var scope = VendorScope(_tenantA, _v1);
        var db = scope.ServiceProvider.GetRequiredService<VendorsDbContext>();

        Assert.Equal([_v1], await db.Vendors.AsNoTracking().Select(v => v.Id).ToListAsync());
        Assert.All(await db.Requests.AsNoTracking().ToListAsync(), r => Assert.Equal(_v1, r.VendorId));
        Assert.All(await db.Messages.AsNoTracking().ToListAsync(), r => Assert.Equal(_v1, r.VendorId));
        Assert.All(await db.BankDetails.AsNoTracking().ToListAsync(), r => Assert.Equal(_v1, r.VendorId));
        Assert.All(await db.BankDetailChanges.AsNoTracking().ToListAsync(), r => Assert.Equal(_v1, r.VendorId));
        Assert.NotEmpty(await db.Requests.AsNoTracking().ToListAsync());
        Assert.NotEmpty(await db.BankDetailChanges.AsNoTracking().ToListAsync());

        // asking for another vendor's row by id finds nothing, whatever the id
        Assert.Null(await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == _v2));
        Assert.Null(await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == _vB));
        Assert.Empty(await db.Requests.AsNoTracking().Where(r => r.VendorId == _v2).ToListAsync());
    }

    [Fact]
    public async Task A_staff_scope_still_sees_every_vendor_of_its_tenant_and_no_other_tenant()
    {
        await using var scope = _host.Scope(_tenantA, _staff);
        var ids = await scope.ServiceProvider.GetRequiredService<VendorsDbContext>().Vendors.AsNoTracking().Select(v => v.Id).ToListAsync();
        Assert.Equivalent(new[] { _v1, _v2 }, ids);
    }

    [Fact]
    public async Task A_vendor_scope_cannot_write_rows_for_another_vendor()
    {
        await using var scope = VendorScope(_tenantA, _v1);
        var db = scope.ServiceProvider.GetRequiredService<VendorsDbContext>();
        db.Requests.Add(new VendorRequest { Id = Guid.NewGuid(), VendorId = _v2, Subject = "planted", CreatedBy = _staff, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_vendor_of_a_scope_cannot_be_swapped()
    {
        await using var scope = VendorScope(_tenantA, _v1);
        Assert.Throws<TenantViolationException>(() => scope.ServiceProvider.GetRequiredService<VendorContext>().SetVendor(_v2));
    }

    [Fact]
    public async Task The_portal_service_works_on_the_session_vendor_and_treats_other_ids_as_unknown()
    {
        Guid theirRequest;
        await using (var staffScope = _host.Scope(_tenantA, _staff))
        {
            theirRequest = (await staffScope.ServiceProvider.GetRequiredService<IVendorRequestService>().ListAsync(_v2)).Single().Id;
        }

        await using var scope = VendorScope(_tenantA, _v1);
        var portal = scope.ServiceProvider.GetRequiredService<IVendorPortalService>();
        Assert.Equal(_v1, (await portal.GetProfileAsync()).Id);
        Assert.All(await portal.ListRequestsAsync(), r => Assert.Equal(_v1, r.VendorId));
        await Assert.ThrowsAsync<NotFoundException>(() => portal.ReplyToRequestAsync(theirRequest, "hello"));
        await Assert.ThrowsAsync<NotFoundException>(() => portal.OpenDocumentAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_shared_account_control_sees_across_vendors_even_in_a_vendor_scope()
    {
        var iban = "SA0380000000608010167519";
        await using (var staffScope = _host.Scope(_tenantA, _staff))
        {
            await staffScope.ServiceProvider.GetRequiredService<IBankDetailsService>().ProposeAsync(_v2, Bank(iban, "USD"));
        }

        await using var scope = VendorScope(_tenantA, _v1, Guid.NewGuid());
        var ex = await Assert.ThrowsAsync<ConflictException>(() => scope.ServiceProvider.GetRequiredService<IVendorPortalService>().ProposeBankDetailsAsync(Bank(iban, "USD")));
        Assert.Equal("bank_details_not_accepted", ex.Code);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private static ProposeBankDetails Bank(string iban, string currency = "SAR") => new("Acme LLC", "National Bank", "NCBKSAJE", "SA", currency, iban, null, "Onboarding");

    private AsyncServiceScope VendorScope(Guid tenant, Guid vendor, Guid? user = null)
    {
        var scope = _host.Scope(tenant, user ?? _staff);
        scope.ServiceProvider.GetRequiredService<VendorContext>().SetVendor(vendor);
        return scope;
    }

    private async Task<Guid> Seed(Guid tenant, string name)
    {
        await using var scope = _host.Scope(tenant, _staff);
        var vendor = await scope.ServiceProvider.GetRequiredService<IVendorService>().CreateAsync(new CreateVendor(name, null, null, "SA", null, null, null, null));
        await scope.ServiceProvider.GetRequiredService<IVendorRequestService>().CreateAsync(vendor.Id, "Please confirm", "Hello " + name);
        if (tenant == _tenantA && name == "Vendor One")
        {
            await scope.ServiceProvider.GetRequiredService<IBankDetailsService>().ProposeAsync(vendor.Id, Bank("SA4420000001234567891234"));
        }

        return vendor.Id;
    }
}
