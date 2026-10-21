using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.Identity.Application;
using Vpms.Identity.Domain;

namespace Vpms.Identity.Infrastructure;

public sealed class ProviderAdminService(
    IdentityDbContext db, ISecretResolver secrets, IProviderChangeNotifier notifier, IAuditPort audit) : IProviderAdminService
{
    private static readonly Regex Domain = new(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", RegexOptions.Compiled);
    private static readonly Regex SecretRef = new(@"^[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<ProviderDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.IdentityProviders.AsNoTracking().OrderBy(p => p.DisplayName).ToListAsync(ct);
        var result = new List<ProviderDto>();
        foreach (var row in rows)
        {
            result.Add(await MapAsync(row, ct));
        }

        return result;
    }

    public async Task<ProviderDto> CreateAsync(SaveProvider request, CancellationToken ct = default)
    {
        var provider = new IdentityProviderConfig { Id = Guid.NewGuid(), Enabled = true };
        Apply(provider, request);
        db.IdentityProviders.Add(provider);
        audit.Record(new AuditEntry("provider.created", "IdentityProvider", provider.Id.ToString(), ChangeSummaryJson: Summary(provider)));
        await db.SaveChangesAsync(ct);
        return await MapAsync(provider, ct);
    }

    public async Task<ProviderDto> UpdateAsync(Guid id, SaveProvider request, CancellationToken ct = default)
    {
        var provider = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Identity provider");
        Apply(provider, request);
        audit.Record(new AuditEntry("provider.updated", "IdentityProvider", id.ToString(), ChangeSummaryJson: Summary(provider)));
        await db.SaveChangesAsync(ct);
        notifier.ProviderChanged(id);
        return await MapAsync(provider, ct);
    }

    public async Task<ProviderDto> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var provider = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Identity provider");
        if (provider.Enabled != enabled)
        {
            provider.Enabled = enabled;
            audit.Record(new AuditEntry(enabled ? "provider.enabled" : "provider.disabled", "IdentityProvider", id.ToString()));
            await db.SaveChangesAsync(ct);
            notifier.ProviderChanged(id);
        }

        return await MapAsync(provider, ct);
    }

    private static void Apply(IdentityProviderConfig p, SaveProvider r)
    {
        if (!Enum.IsDefined(r.Kind))
        {
            throw new ValidationFailedException("invalid_kind", "Unknown provider type.");
        }

        if (string.IsNullOrWhiteSpace(r.DisplayName) || r.DisplayName.Length > 200)
        {
            throw new ValidationFailedException("invalid_name", "The display name is required (max 200 characters).");
        }

        if (!Uri.TryCreate(r.Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps
            || !IdentityProviderConfig.IsAuthorityAcceptable(r.Kind, r.Authority))
        {
            throw new ValidationFailedException("invalid_authority",
                r.Kind == IdentityProviderKind.EntraId
                    ? "Use an https authority for your own directory, e.g. https://login.microsoftonline.com/<directory-id>/v2.0 (not common/organizations/consumers)."
                    : "The authority must be an https URL.");
        }

        if (string.IsNullOrWhiteSpace(r.ClientId) || r.ClientId.Length > 200)
        {
            throw new ValidationFailedException("invalid_client_id", "The client id is required.");
        }

        if (!SecretRef.IsMatch(r.ClientSecretRef ?? ""))
        {
            throw new ValidationFailedException("invalid_secret_ref", "Give the NAME of the secret in the vault, not the secret itself.");
        }

        var scopes = (r.Scopes ?? "openid profile email").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains("openid") || scopes.Length > 20)
        {
            throw new ValidationFailedException("invalid_scopes", "Scopes must include 'openid'.");
        }

        var domains = (r.AllowedEmailDomains ?? []).Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();
        if (domains.Any(d => !Domain.IsMatch(d)))
        {
            throw new ValidationFailedException("invalid_domain", "Email domains must look like example.com.");
        }

        if (r.AutoProvision && domains.Count == 0)
        {
            throw new ValidationFailedException("domains_required", "Automatic sign-up needs at least one allowed email domain.");
        }

        p.Kind = r.Kind;
        p.DisplayName = r.DisplayName.Trim();
        p.Authority = r.Authority.TrimEnd('/');
        p.ClientId = r.ClientId.Trim();
        p.ClientSecretRef = r.ClientSecretRef!;
        p.Scopes = string.Join(' ', scopes);
        p.AutoProvision = r.AutoProvision;
        p.AllowedEmailDomains = string.Join(',', domains);
    }

    private static string Summary(IdentityProviderConfig p) => JsonSerializer.Serialize(new
    {
        p.Kind, p.DisplayName, p.Authority, p.ClientId, p.ClientSecretRef, p.AutoProvision, p.AllowedEmailDomains,
    });

    private async Task<ProviderDto> MapAsync(IdentityProviderConfig p, CancellationToken ct) =>
        new(p.Id, p.Kind, p.DisplayName, p.Authority, p.ClientId, p.ClientSecretRef, p.Scopes, p.Enabled, p.AutoProvision,
            p.AllowedEmailDomains.Split(',', StringSplitOptions.RemoveEmptyEntries), await secrets.ResolveAsync(p.ClientSecretRef, ct) is not null);
}
