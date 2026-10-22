using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Identity.Domain;
using Vpms.Identity.Infrastructure;

namespace Vpms.Migrator;

public sealed record ProvisionRequest(
    string Name, string Slug, string AdminEmail, IdentityProviderKind ProviderKind, string ProviderName, string Authority,
    string ClientId, string ClientSecretRef, IReadOnlyList<string> AllowedEmailDomains, string DefaultLocale = "en");

public sealed record ProvisionResult(Guid TenantId, Guid ProviderId);

/// <summary>
/// Operator command that creates a customer: tenant, default roles, the first identity provider, and the role the first
/// administrator receives when they sign in. There is deliberately no web surface for this.
/// </summary>
public static class TenantProvisioner
{
    private static readonly Regex Slug = new("^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$", RegexOptions.Compiled);

    public static async Task<ProvisionResult> ProvisionAsync(string ownerConnectionString, ProvisionRequest r, CancellationToken ct = default)
    {
        Validate(r);
        var tenantId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var tenant = new TenantContext();
        tenant.SetTenant(tenantId);

        await using var customer = Create<CustomerAdminDbContext>(ownerConnectionString, tenant, o => new CustomerAdminDbContext(o, tenant));
        await using var identity = Create<IdentityDbContext>(ownerConnectionString, tenant, o => new IdentityDbContext(o, tenant));
        await using var auditDb = Create<AuditDbContext>(ownerConnectionString, tenant, o => new AuditDbContext(o, tenant));

        using (tenant.BeginPlatformScope())
        {
            if (await customer.Tenants.AnyAsync(t => t.Slug == r.Slug, ct))
            {
                throw new InvalidOperationException($"A tenant with slug '{r.Slug}' already exists.");
            }
        }

        customer.Tenants.Add(new Tenant { Id = tenantId, Name = r.Name.Trim(), Slug = r.Slug, DefaultLocale = r.DefaultLocale, CreatedAt = DateTimeOffset.UtcNow });
        await customer.SaveChangesAsync(ct);

        Guid adminRole = default;
        foreach (var (name, permissions) in SystemRoles.Definitions)
        {
            var role = new Role
            {
                Id = Guid.NewGuid(), Name = name, IsSystem = true,
                Permissions = permissions.Select(p => new RolePermission { Id = Guid.NewGuid(), Permission = p }).ToList(),
            };
            if (name == "Tenant Administrator") adminRole = role.Id;
            identity.Roles.Add(role);
        }

        identity.IdentityProviders.Add(new IdentityProviderConfig
        {
            Id = providerId, Kind = r.ProviderKind, DisplayName = r.ProviderName, Authority = r.Authority.TrimEnd('/'),
            ClientId = r.ClientId, ClientSecretRef = r.ClientSecretRef, Enabled = true, AutoProvision = true,
            AllowedEmailDomains = string.Join(',', r.AllowedEmailDomains.Select(d => d.ToLowerInvariant())),
        });
        identity.PendingRoleAssignments.Add(new PendingRoleAssignment
        {
            Id = Guid.NewGuid(), Email = r.AdminEmail.Trim().ToLowerInvariant(), RoleId = adminRole, CreatedBy = Guid.Empty, CreatedAt = DateTimeOffset.UtcNow,
        });
        await identity.SaveChangesAsync(ct);

        var port = new AuditPort(new AuditBuffer(), new AuditContext { ActorType = AuditActorType.System, CorrelationId = "provision-tenant" },
            tenant, auditDb, new SqlAuditAppender(TimeProvider.System));
        await port.WriteNowAsync(new AuditEntry("tenant.provisioned", "Tenant", tenantId.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { r.Slug, providerId, firstAdmin = "pending-email" }), TenantId: tenantId), ct);

        return new ProvisionResult(tenantId, providerId);
    }

    private static void Validate(ProvisionRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) throw new ArgumentException("Name is required (max 200).");
        if (!Slug.IsMatch(r.Slug)) throw new ArgumentException("Slug must be lower-case letters, digits and hyphens.");
        if (!Regex.IsMatch(r.AdminEmail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new ArgumentException("Admin email is invalid.");
        if (r.AllowedEmailDomains.Count == 0) throw new ArgumentException("At least one allowed email domain is required for the first sign-in.");
        if (!Uri.TryCreate(r.Authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !IdentityProviderConfig.IsAuthorityAcceptable(r.ProviderKind, r.Authority))
        {
            throw new ArgumentException("Authority must be an https URL (tenant-specific for Entra ID).");
        }

        var adminDomain = r.AdminEmail[(r.AdminEmail.LastIndexOf('@') + 1)..].ToLowerInvariant();
        if (!r.AllowedEmailDomains.Select(d => d.ToLowerInvariant()).Contains(adminDomain))
        {
            throw new ArgumentException("The first administrator's email domain must be in the allowed domains.");
        }
    }

    private static T Create<T>(string cs, TenantContext tenant, Func<DbContextOptions<T>, T> factory) where T : DbContext =>
        factory(new DbContextOptionsBuilder<T>().UseSqlServer(cs).AddInterceptors(new TenantSessionContextInterceptor(tenant)).Options);
}
