using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Identity.Application;
using Vpms.Identity.Contracts;
using Vpms.Identity.Domain;
using Vpms.Identity.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class RbacTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _entity1 = Guid.NewGuid();
    private readonly Guid _entity2 = Guid.NewGuid();
    private Guid _userA;
    private Guid _userB;

    public RbacTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(await _sql.CreateDatabaseAsync());
        (_userA, _) = await SeedTenantAsync(_tenantA);
        (_userB, _) = await SeedTenantAsync(_tenantB);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Entity_scoped_role_grants_the_permission_only_for_that_entity()
    {
        await Run(_tenantA, async (svc, eval, db) =>
        {
            await svc.AssignAsync(_userA, _userA, await RoleId(db, "Invoice Approver"), _entity1);

            Assert.True(await eval.HasAsync(_userA, Permissions.InvoiceApprove, _entity1));
            Assert.False(await eval.HasAsync(_userA, Permissions.InvoiceApprove, _entity2));
            Assert.False(await eval.HasAsync(_userA, Permissions.InvoiceApprove), "not tenant-wide");

            var scope = await eval.GetScopeAsync(_userA, Permissions.InvoiceApprove);
            Assert.False(scope.AllEntities);
            Assert.Equal([_entity1], scope.EntityIds);
        });
    }

    [Fact]
    public async Task Tenant_wide_role_applies_to_every_entity()
    {
        await Run(_tenantA, async (svc, eval, db) =>
        {
            await svc.AssignAsync(_userA, _userA, await RoleId(db, "Auditor"), null);
            Assert.True(await eval.HasAsync(_userA, Permissions.AuditRead));
            Assert.True(await eval.HasAsync(_userA, Permissions.AuditRead, _entity2));
        });
    }

    [Fact]
    public async Task Users_without_roles_have_no_permissions()
    {
        await Run(_tenantA, async (_, eval, _) =>
        {
            Assert.False(await eval.HasAsync(_userA, Permissions.AuditRead));
            Assert.Same(PermissionScope.None, await eval.GetScopeAsync(_userA, Permissions.AuditRead));
        });
    }

    [Fact]
    public async Task Conflicting_roles_cannot_be_assigned_for_the_same_entity()
    {
        await Run(_tenantA, async (svc, _, db) =>
        {
            await svc.AssignAsync(_userA, _userA, await RoleId(db, "Invoice Approver"), _entity1);
            await Assert.ThrowsAsync<SegregationOfDutiesException>(async () =>
                await svc.AssignAsync(_userA, _userA, await RoleId(db, "Payment Releaser"), _entity1));
            // A different entity is a different scope, so the same person may release payments there.
            await svc.AssignAsync(_userA, _userA, await RoleId(db, "Payment Releaser"), _entity2);
        });
    }

    [Fact]
    public async Task Tenant_wide_grant_conflicts_with_an_existing_entity_scoped_grant()
    {
        await Run(_tenantA, async (svc, _, db) =>
        {
            await svc.AssignAsync(_userA, _userA, await RoleId(db, "Invoice Approver"), _entity1);
            await Assert.ThrowsAsync<SegregationOfDutiesException>(async () =>
                await svc.AssignAsync(_userA, _userA, await RoleId(db, "Payment Releaser"), null));
        });
    }

    [Fact]
    public async Task Bank_details_maker_and_checker_roles_cannot_be_combined()
    {
        await Run(_tenantA, async (svc, _, db) =>
        {
            await svc.AssignAsync(_userA, _userA, await RoleId(db, "Vendor Manager"), null);
            await Assert.ThrowsAsync<SegregationOfDutiesException>(async () =>
                await svc.AssignAsync(_userA, _userA, await RoleId(db, "Bank Details Approver"), _entity1));
        });
    }

    [Fact]
    public async Task Another_tenants_roles_and_users_are_invisible()
    {
        Guid tenantBRole = default;
        await Run(_tenantB, async (_, _, db) => tenantBRole = await RoleId(db, "Auditor"));

        await Run(_tenantA, async (svc, eval, db) =>
        {
            Assert.DoesNotContain(await db.Roles.ToListAsync(), r => r.TenantId == _tenantB);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync(_userA, _userA, tenantBRole, null));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await svc.AssignAsync(_userA, _userB, await RoleId(db, "Auditor"), null));
        });
    }

    [Fact]
    public async Task Assignments_of_another_tenants_user_grant_nothing_here()
    {
        await Run(_tenantB, async (svc, _, db) => await svc.AssignAsync(_userB, _userB, await RoleId(db, "Auditor"), null));
        await Run(_tenantA, async (_, eval, _) => Assert.False(await eval.HasAsync(_userB, Permissions.AuditRead)));
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private static async Task<Guid> RoleId(IdentityDbContext db, string name) =>
        (await db.Roles.FirstAsync(r => r.Name == name)).Id;

    private async Task<(Guid UserId, Guid RoleId)> SeedTenantAsync(Guid tenantId)
    {
        var userId = Guid.NewGuid();
        await Run(tenantId, async (svc, _, db) =>
        {
            db.Users.Add(new User { Id = userId, ProviderId = Guid.NewGuid(), ExternalSubject = "s" + userId, Email = "u@x.com", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await svc.EnsureSystemRolesAsync();
        });
        return (userId, Guid.Empty);
    }

    private async Task Run(Guid tenantId, Func<IRoleAssignmentService, IPermissionEvaluator, IdentityDbContext, Task> action)
    {
        await using var scope = _host.Scope(tenantId);
        var sp = scope.ServiceProvider;
        await action(sp.GetRequiredService<IRoleAssignmentService>(), sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<IdentityDbContext>());
    }
}
