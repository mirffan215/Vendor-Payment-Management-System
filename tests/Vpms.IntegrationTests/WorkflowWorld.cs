using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Identity.Application;
using Vpms.Identity.Domain;
using Vpms.Identity.Infrastructure;
using Vpms.TestSupport;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;

namespace Vpms.IntegrationTests;

/// <summary>Tenants, legal entities, users with system roles, and matrix data for the workflow integration tests.</summary>
public sealed class WorkflowWorld(IntegrationHost host)
{
    public IntegrationHost Host { get; } = host;
    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid Admin { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        foreach (var t in new[] { TenantA, TenantB })
        {
            await using var scope = Host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var admin = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            admin.Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await admin.SaveChangesAsync();
            await using var inner = Host.Scope(t);
            await inner.ServiceProvider.GetRequiredService<IRoleAssignmentService>().EnsureSystemRolesAsync();
        }
    }

    public Task<Guid> EntityAsync(Guid tenant, string code, string country = "SA", string currency = "SAR") =>
        ActiveVendorKit.CreateEntityAsync(Host.Services, tenant, Admin, code, country, currency);

    /// <summary>A user holding the named system roles, tenant-wide when entity is null.</summary>
    public async Task<Guid> UserAsync(Guid tenant, string email, Guid? entity, params string[] roles)
    {
        var id = Guid.NewGuid();
        await using var scope = Host.Scope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db.Users.Add(new User { Id = id, ProviderId = Guid.NewGuid(), ExternalSubject = "s" + id, Email = email, DisplayName = email, CreatedAt = DateTimeOffset.UtcNow });
        foreach (var name in roles)
        {
            var role = await db.Roles.FirstAsync(r => r.Name == name);
            db.RoleAssignments.Add(new RoleAssignment { Id = Guid.NewGuid(), UserId = id, RoleId = role.Id, LegalEntityId = entity, AssignedAt = DateTimeOffset.UtcNow, AssignedBy = Admin });
        }

        await db.SaveChangesAsync();
        return id;
    }

    public Task<T> AsAsync<T>(Guid tenant, Guid actor, Func<IServiceProvider, Task<T>> action) => ActiveVendorKit.AsAsync(Host.Services, tenant, actor, action);

    public Task<T> Workflows<T>(Guid tenant, Guid actor, Func<IWorkflowDefinitionService, Task<T>> action) =>
        AsAsync(tenant, actor, s => action(s.GetRequiredService<IWorkflowDefinitionService>()));

    /// <summary>Gives the user an approval authority for the entity (default: any amount up to 1,000,000 SAR, no dimension limits).</summary>
    public Task AuthorityAsync(Guid tenant, Guid entity, Guid user, decimal max = 1_000_000m, string currency = "SAR", string? dimensions = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        AsAsync(tenant, Admin, async s =>
        {
            await s.GetRequiredService<IApprovalAdminService>().CreateAuthorityAsync(
                new CreateApprovalAuthority(entity, user, max, currency, dimensions, from ?? DateTimeOffset.UtcNow.AddDays(-1), to));
            return 0;
        });

    public Task MatrixRuleAsync(Guid tenant, Guid entity, string name, int priority, string conditionsJson, string resolutionJson, int levels) =>
        AsAsync(tenant, Admin, async s =>
        {
            await s.GetRequiredService<IApprovalAdminService>().CreateRuleAsync(new CreateApprovalMatrixRule(entity, name, priority, conditionsJson, resolutionJson, levels));
            return 0;
        });

    public static ApproverSpec User(Guid id) => new(ApproverKind.User, id.ToString());

    public static ApproverSpec Role(string name) => new(ApproverKind.Role, name);

    public static WorkflowDefinitionSpec TwoLevels(Guid first, Guid second) => new()
    {
        Name = "Two levels", Start = "l1",
        Steps =
        [
            new StepSpec { Id = "l1", Approvers = [new ApproverSpec(ApproverKind.User, first.ToString())], Next = "l2" },
            new StepSpec { Id = "l2", Approvers = [new ApproverSpec(ApproverKind.User, second.ToString())] },
        ],
    };

    public static string Level(string role, int? due = null) =>
        $"{{\"approvers\":[{{\"kind\":\"Role\",\"value\":\"{role}\"}}]{(due is null ? "" : $",\"dueHours\":{due}")}}}";

    public static string Levels(params string[] levels) => $"{{\"levels\":[{string.Join(',', levels)}]}}";
}
