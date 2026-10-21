using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Identity.Domain;

public enum IdentityProviderKind
{
    EntraId = 1,
    Google = 2,
    GenericOidc = 3,
}

/// <summary>An external identity provider a tenant's users sign in with. Secrets are referenced, never stored.</summary>
public class IdentityProviderConfig : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public IdentityProviderKind Kind { get; set; }
    public string DisplayName { get; set; } = "";

    /// <summary>OIDC authority. For Entra ID this must be tenant-specific (never common/organizations/consumers).</summary>
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";

    /// <summary>Name of the secret in the secret store (Key Vault); the value is never kept in the database.</summary>
    public string ClientSecretRef { get; set; } = "";
    public string Scopes { get; set; } = "openid profile email";
    public bool Enabled { get; set; } = true;
    public bool AutoProvision { get; set; }

    /// <summary>Comma-separated email domains allowed to sign in or be auto-provisioned; empty means none.</summary>
    public string AllowedEmailDomains { get; set; } = "";

    public static bool IsAuthorityAcceptable(IdentityProviderKind kind, string authority)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (kind == IdentityProviderKind.EntraId)
        {
            var path = uri.AbsolutePath.Trim('/').Split('/')[0].ToLowerInvariant();
            if (path is "" or "common" or "organizations" or "consumers")
            {
                return false;
            }
        }

        return true;
    }
}
