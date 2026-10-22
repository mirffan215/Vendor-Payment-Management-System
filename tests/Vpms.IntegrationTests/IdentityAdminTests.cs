using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Identity.Application;
using Vpms.Identity.Contracts;
using Vpms.Identity.Domain;
using Vpms.Identity.Infrastructure;
using Vpms.Migrator;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class IdentityAdminTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private TestDatabase _db = null!;
    private readonly RecordingNotifier _notifier = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private Guid _admin;
    private Guid _second;

    public IdentityAdminTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _db = await _sql.CreateDatabaseAsync();
        _host = new IntegrationHost(_db, configure: s => s.AddSingleton<IProviderChangeNotifier>(_notifier));
        foreach (var t in new[] { _tenant, _other })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            db.Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await using var inner = _host.Scope(t);
            await inner.ServiceProvider.GetRequiredService<IRoleAssignmentService>().EnsureSystemRolesAsync();
        }

        _admin = await SeedUser(_tenant, "admin@t.com");
        _second = await SeedUser(_tenant, "second@t.com");
        await Seed(_tenant, async s => await s.GetRequiredService<IRoleAssignmentService>().AssignAsync(_second, _admin, await RoleId(s, "Tenant Administrator"), null));
    }


    public async Task DisposeAsync() => await _host.DisposeAsync();

    // --- roles ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Custom_roles_can_be_created_changed_while_unassigned_and_deleted()
    {
        var role = await As(_admin, s => s.GetRequiredService<IRoleAdminService>().CreateRoleAsync("AP Reviewer", [Permissions.VendorView, Permissions.InvoiceCreate]));
        Assert.False(role.IsSystem);

        var updated = await As(_admin, s => s.GetRequiredService<IRoleAdminService>().UpdateRolePermissionsAsync(role.Id, [Permissions.VendorView]));
        Assert.Equal([Permissions.VendorView], updated.Permissions);

        await AsVoid(_admin, s => s.GetRequiredService<IRoleAdminService>().DeleteRoleAsync(role.Id));
        Assert.DoesNotContain(await As(_admin, s => s.GetRequiredService<IRoleAdminService>().ListRolesAsync()), r => r.Id == role.Id);
    }

    [Fact]
    public async Task Roles_cannot_bundle_conflicting_duties_or_unknown_permissions()
    {
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>()
            .CreateRoleAsync("Bad", [Permissions.InvoiceApprove, Permissions.PaymentRelease])));
        var unknown = await Assert.ThrowsAsync<ValidationFailedException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>()
            .CreateRoleAsync("Odd", ["Everything.Allowed"])));
        Assert.Equal("unknown_permission", unknown.Code);
        var empty = await Assert.ThrowsAsync<ValidationFailedException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>().CreateRoleAsync("None", [])));
        Assert.Equal("no_permissions", empty.Code);
    }

    [Fact]
    public async Task System_roles_and_roles_in_use_are_protected()
    {
        var roles = await As(_admin, s => s.GetRequiredService<IRoleAdminService>().ListRolesAsync());
        var system = roles.First(r => r.IsSystem);
        Assert.Equal("system_role", (await Assert.ThrowsAsync<ConflictException>(() => AsVoid(_admin, s => s.GetRequiredService<IRoleAdminService>().DeleteRoleAsync(system.Id)))).Code);
        Assert.Equal("system_role", (await Assert.ThrowsAsync<ConflictException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>().UpdateRolePermissionsAsync(system.Id, [Permissions.AuditRead])))).Code);

        var custom = await As(_admin, s => s.GetRequiredService<IRoleAdminService>().CreateRoleAsync("Reader", [Permissions.AuditRead]));
        var user = await SeedUser(_tenant, "reader@t.com");
        await As(_admin, s => s.GetRequiredService<IRoleAdminService>().AssignAsync(user, custom.Id, null));
        Assert.Equal("role_in_use", (await Assert.ThrowsAsync<ConflictException>(() => AsVoid(_admin, s => s.GetRequiredService<IRoleAdminService>().DeleteRoleAsync(custom.Id)))).Code);
        Assert.Equal("role_in_use", (await Assert.ThrowsAsync<ConflictException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>().UpdateRolePermissionsAsync(custom.Id, [Permissions.VendorView])))).Code);
    }

    [Fact]
    public async Task Nobody_can_assign_a_role_to_themselves()
    {
        var role = await As(_admin, s => s.GetRequiredService<IRoleAdminService>().CreateRoleAsync("Reader", [Permissions.AuditRead]));
        var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>().AssignAsync(_admin, role.Id, null)));
        Assert.Equal("maker-checker", ex.Rule);
    }

    [Fact]
    public async Task Assignments_check_conflicts_scope_and_duplicates()
    {
        var user = await SeedUser(_tenant, "u@t.com");
        var entity = await CreateEntity(_tenant, "E1");
        var roles = (await As(_admin, s => s.GetRequiredService<IRoleAdminService>().ListRolesAsync())).ToDictionary(r => r.Name);

        await As(_admin, s => s.GetRequiredService<IRoleAdminService>().AssignAsync(user, roles["Invoice Approver"].Id, entity));
        Assert.Equal("assignment_exists", (await Assert.ThrowsAsync<ConflictException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>()
            .AssignAsync(user, roles["Invoice Approver"].Id, entity)))).Code);
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>()
            .AssignAsync(user, roles["Payment Releaser"].Id, entity)));
        Assert.Equal("unknown_legal_entity", (await Assert.ThrowsAsync<ValidationFailedException>(() => As(_admin, s => s.GetRequiredService<IRoleAdminService>()
            .AssignAsync(user, roles["Auditor"].Id, Guid.NewGuid())))).Code);

        var list = await As(_admin, s => s.GetRequiredService<IRoleAdminService>().ListAssignmentsAsync(user));
        Assert.Equal(["Invoice Approver"], list.Select(a => a.RoleName));
    }

    [Fact]
    public async Task The_last_tenant_administrator_cannot_be_revoked_or_disabled()
    {
        // `_admin` is the only administrator; `_second` is an ordinary user who manages roles in this test.
        var adminAssignment = (await As(_second, s => s.GetRequiredService<IRoleAdminService>().ListAssignmentsAsync(_admin)))
            .Single(a => a.RoleName == "Tenant Administrator");

        var revoke = await Assert.ThrowsAsync<ConflictException>(() => AsVoid(_second, s => s.GetRequiredService<IRoleAdminService>().RevokeAsync(adminAssignment.Id)));
        Assert.Equal("last_tenant_admin", revoke.Code);
        var disable = await Assert.ThrowsAsync<ConflictException>(() => As(_second, s => s.GetRequiredService<IUserAdminService>().SetStatusAsync(_admin, false)));
        Assert.Equal("last_tenant_admin", disable.Code);

        // Once someone else is an administrator too, the first can be revoked.
        await Seed(_tenant, async s => await s.GetRequiredService<IRoleAssignmentService>().AssignAsync(_admin, _second, await RoleId(s, "Tenant Administrator"), null));
        await AsVoid(_second, s => s.GetRequiredService<IRoleAdminService>().RevokeAsync(adminAssignment.Id));
    }

    // --- users ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Users_can_be_disabled_and_enabled_but_not_by_themselves()
    {
        var user = await SeedUser(_tenant, "worker@t.com");
        var disabled = await As(_admin, s => s.GetRequiredService<IUserAdminService>().SetStatusAsync(user, false));
        Assert.Equal("Disabled", disabled.Status);
        Assert.Equal("Active", (await As(_admin, s => s.GetRequiredService<IUserAdminService>().SetStatusAsync(user, true))).Status);

        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(user, s => s.GetRequiredService<IUserAdminService>().SetStatusAsync(user, false)));
    }

    [Fact]
    public async Task Users_are_listed_with_roles_searched_and_never_across_tenants()
    {
        await SeedUser(_other, "stranger@o.com");
        var all = await As(_admin, s => s.GetRequiredService<IUserAdminService>().ListAsync(null, 0, 50));
        Assert.DoesNotContain(all, u => u.Email == "stranger@o.com");
        Assert.Contains("Tenant Administrator", all.Single(u => u.Id == _admin).Roles);

        var found = await As(_admin, s => s.GetRequiredService<IUserAdminService>().ListAsync("second", 0, 50));
        Assert.Equal(["second@t.com"], found.Select(u => u.Email));

        var directory = await As(_admin, s => s.GetRequiredService<IUserDirectory>().GetAsync([_second, Guid.NewGuid()]));
        Assert.Single(directory);
    }

    // --- identity providers --------------------------------------------------------------------------------

    [Fact]
    public async Task Providers_are_validated_audited_and_the_runtime_scheme_is_refreshed_on_change()
    {
        var request = new SaveProvider(IdentityProviderKind.GenericOidc, "Corporate SSO", "https://idp.example.com/", "client-1", "acme-sso-secret",
            null, true, ["Acme.com", " acme.com "]);
        var created = await As(_admin, s => s.GetRequiredService<IProviderAdminService>().CreateAsync(request));
        Assert.Equal("https://idp.example.com", created.Authority);
        Assert.Equal(["acme.com"], created.AllowedEmailDomains);
        Assert.False(created.SecretAvailable);
        Assert.Empty(_notifier.Changed);

        await As(_admin, s => s.GetRequiredService<IProviderAdminService>().UpdateAsync(created.Id, request with { DisplayName = "SSO v2" }));
        await As(_admin, s => s.GetRequiredService<IProviderAdminService>().SetEnabledAsync(created.Id, false));
        Assert.Equal([created.Id, created.Id], _notifier.Changed);

        await using var scope = _host.Scope(_tenant);
        var actions = (await scope.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(EntityType: "IdentityProvider", Limit: 20), null)).Items.Select(e => e.Action);
        Assert.Equal(["provider.created", "provider.updated", "provider.disabled"], actions);
    }

    [Theory]
    [InlineData("http://idp.example.com", "invalid_authority")]
    [InlineData("not a url", "invalid_authority")]
    public async Task Provider_authorities_must_be_https(string authority, string expected)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => As(_admin, s => s.GetRequiredService<IProviderAdminService>().CreateAsync(
            new SaveProvider(IdentityProviderKind.GenericOidc, "X", authority, "c", "secret", null, false, null))));
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task Entra_providers_must_name_a_specific_directory()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => As(_admin, s => s.GetRequiredService<IProviderAdminService>().CreateAsync(
            new SaveProvider(IdentityProviderKind.EntraId, "X", "https://login.microsoftonline.com/common/v2.0", "c", "secret", null, false, null))));
        Assert.Equal("invalid_authority", ex.Code);
        await As(_admin, s => s.GetRequiredService<IProviderAdminService>().CreateAsync(
            new SaveProvider(IdentityProviderKind.EntraId, "X", "https://login.microsoftonline.com/6f1f8c1e-0000-0000-0000-000000000000/v2.0", "c", "secret", null, false, null)));
    }

    [Theory]
    [InlineData("secret value with spaces", "openid", false, "acme.com", "invalid_secret_ref")]
    [InlineData("secret", "profile email", false, "acme.com", "invalid_scopes")]
    [InlineData("secret", "openid", true, "", "domains_required")]
    [InlineData("secret", "openid", false, "not a domain", "invalid_domain")]
    public async Task Provider_settings_are_validated(string secretRef, string scopes, bool auto, string domain, string expected)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => As(_admin, s => s.GetRequiredService<IProviderAdminService>().CreateAsync(
            new SaveProvider(IdentityProviderKind.GenericOidc, "X", "https://idp.example.com", "c", secretRef, scopes, auto, domain.Length == 0 ? [] : [domain]))));
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task Providers_of_other_tenants_are_invisible_and_cannot_be_changed()
    {
        var mine = await As(_admin, s => s.GetRequiredService<IProviderAdminService>().CreateAsync(
            new SaveProvider(IdentityProviderKind.GenericOidc, "Mine", "https://idp.example.com", "c", "secret", null, false, null)));
        var theirAdmin = await SeedUser(_other, "admin@o.com");
        await using var scope = _host.Scope(_other, theirAdmin);
        var svc = scope.ServiceProvider.GetRequiredService<IProviderAdminService>();
        Assert.Empty(await svc.ListAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => svc.SetEnabledAsync(mine.Id, false));
    }

    // --- first administrator -------------------------------------------------------------------------------

    [Fact]
    public async Task Provisioning_a_tenant_makes_the_named_person_administrator_on_first_sign_in()
    {
        var result = await TenantProvisioner.ProvisionAsync(_db.OwnerConnectionString, new ProvisionRequest(
            "Beta Group", "beta", "Boss@Beta.com", IdentityProviderKind.GenericOidc, "Beta SSO", "https://idp.beta.test", "client-b", "beta-secret", ["beta.com"], "ar"));

        await using var scope = _host.Scope(null);
        var provisioner = scope.ServiceProvider.GetRequiredService<UserProvisioner>();
        var store = scope.ServiceProvider.GetRequiredService<IIdentityStore>();
        var provider = (await store.FindProviderAsync(result.ProviderId))!;

        var colleague = await provisioner.SignInAsync(provider, new ExternalIdentity("sub-colleague", "colleague@beta.com", "Colleague"));
        Assert.True(colleague.Succeeded);
        var boss = await provisioner.SignInAsync(provider, new ExternalIdentity("sub-boss", "boss@beta.com", "The Boss"));
        Assert.True(boss.Succeeded);

        await using var read = _host.Scope(result.TenantId);
        var eval = read.ServiceProvider.GetRequiredService<IPermissionEvaluator>();
        Assert.True(await eval.HasAsync(boss.User!.UserId, Permissions.RoleManage));
        Assert.True(await eval.HasAsync(boss.User.UserId, Permissions.ProviderManage));
        Assert.False(await eval.HasAsync(colleague.User!.UserId, Permissions.RoleManage));
        Assert.Empty(await read.ServiceProvider.GetRequiredService<IRoleAdminService>().ListPendingAssignmentsAsync());

        var events = (await read.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Limit: 100), null)).Items.Select(e => e.Action).ToList();
        Assert.Contains("tenant.provisioned", events);
        Assert.Contains("role.pending-applied", events);
    }

    [Fact]
    public async Task Provisioning_rejects_duplicates_and_unsafe_settings()
    {
        ProvisionRequest Req(string slug = "gamma", string admin = "a@gamma.com", string authority = "https://idp.gamma.test", string[]? domains = null) =>
            new("Gamma", slug, admin, IdentityProviderKind.GenericOidc, "SSO", authority, "c", "gamma-secret", domains ?? ["gamma.com"]);

        await TenantProvisioner.ProvisionAsync(_db.OwnerConnectionString, Req());
        await Assert.ThrowsAsync<InvalidOperationException>(() => TenantProvisioner.ProvisionAsync(_db.OwnerConnectionString, Req()));
        await Assert.ThrowsAsync<ArgumentException>(() => TenantProvisioner.ProvisionAsync(_db.OwnerConnectionString, Req(slug: "Bad Slug")));
        await Assert.ThrowsAsync<ArgumentException>(() => TenantProvisioner.ProvisionAsync(_db.OwnerConnectionString, Req(slug: "delta", authority: "http://insecure")));
        await Assert.ThrowsAsync<ArgumentException>(() => TenantProvisioner.ProvisionAsync(_db.OwnerConnectionString, Req(slug: "delta", admin: "a@other.com")));
        await Assert.ThrowsAsync<ArgumentException>(() => TenantProvisioner.ProvisionAsync(_db.OwnerConnectionString, Req(slug: "delta", domains: [])));
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private async Task<T> As<T>(Guid actor, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _host.Scope(_tenant, actor);
        return await action(scope.ServiceProvider);
    }

    private async Task AsVoid(Guid actor, Func<IServiceProvider, Task> action)
    {
        await using var scope = _host.Scope(_tenant, actor);
        await action(scope.ServiceProvider);
    }

    private async Task Seed(Guid tenant, Func<IServiceProvider, Task> action)
    {
        await using var scope = _host.Scope(tenant);
        await action(scope.ServiceProvider);
    }

    private static async Task<Guid> RoleId(IServiceProvider s, string name) =>
        (await s.GetRequiredService<IdentityDbContext>().Roles.FirstAsync(r => r.Name == name)).Id;

    private async Task<Guid> SeedUser(Guid tenant, string email)
    {
        var id = Guid.NewGuid();
        await Seed(tenant, async s =>
        {
            var db = s.GetRequiredService<IdentityDbContext>();
            db.Users.Add(new User { Id = id, ProviderId = Guid.NewGuid(), ExternalSubject = "s" + id, Email = email, DisplayName = email, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        return id;
    }

    private async Task<Guid> CreateEntity(Guid tenant, string code)
    {
        await using var scope = _host.Scope(tenant, _admin);
        return (await scope.ServiceProvider.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity(code, code, null, "SA", "SAR", null))).Id;
    }

    private sealed class RecordingNotifier : IProviderChangeNotifier
    {
        public List<Guid> Changed { get; } = [];

        public void ProviderChanged(Guid providerId) => Changed.Add(providerId);
    }
}
