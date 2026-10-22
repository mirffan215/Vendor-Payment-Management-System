using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.ErpIntegration.Domain;

public static class ConfigKind
{
    public const string Connection = "Connection";
    public const string Mapping = "Mapping";
}

public static class ConfigStatus
{
    public const string Active = "Active";
    public const string Pending = "Pending";
    public const string Superseded = "Superseded";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

public static class ErpProviders
{
    public const string D365Finance = "D365Finance";
    public const string Mock = "Mock";

    public static readonly IReadOnlyList<string> All = [D365Finance, Mock];
}

/// <summary>
/// One version of the tenant's ERP connection (LegalEntityId null) or of one legal entity's ERP mapping. Configuration decides where
/// approved invoices are posted, so every version stays Pending until a different person approves it. Only references to secrets are
/// ever stored, never the secrets.
/// </summary>
public class ErpConfig : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = ConfigKind.Connection;
    public Guid? LegalEntityId { get; set; }
    public int VersionNo { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string Status { get; set; } = ConfigStatus.Pending;
    public string Note { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public void Approve(Guid checker, string? comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "approve an ERP configuration change");
        Status = ConfigStatus.Active;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Reject(Guid checker, string comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "reject an ERP configuration change");
        if (string.IsNullOrWhiteSpace(comment)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = ConfigStatus.Rejected;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Cancel(Guid actor, DateTimeOffset now)
    {
        RequirePending();
        if (actor != CreatedBy) throw new ConflictException("not_the_maker", "Only the person who made the change can cancel it.");
        Status = ConfigStatus.Cancelled;
        DecidedAt = now;
    }

    public void Supersede()
    {
        if (Status != ConfigStatus.Active) throw new ConflictException("invalid_transition", "Only the active version can be superseded.");
        Status = ConfigStatus.Superseded;
    }

    private void RequirePending()
    {
        if (Status != ConfigStatus.Pending) throw new ConflictException("not_pending", "This change has already been decided.");
    }

    private static string? Clean(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)];
}

public sealed record ConfigIssue(string Code, string Message);

/// <param name="SecretRef">The name of the secret holding the client secret. Never the secret itself.</param>
/// <param name="EntitySets">ERP entity-set names by role (for example "VendorInvoiceJournalHeaders"); they differ between ERP versions.</param>
public sealed record ConnectionConfig(
    string Provider, string BaseUrl, string AuthorityTenantId, string ClientId, string SecretRef, IReadOnlyDictionary<string, string> EntitySets, int PageSize = 200, int TimeoutSeconds = 60)
{
    public static readonly IReadOnlyList<string> EntitySetRoles =
        ["vendors", "invoiceJournalHeaders", "invoiceJournalLines", "invoiceAttachments", "vendorInvoices", "vendorPayments", "purchaseOrders", "purchaseOrderLines", "dimensionValues"];

    private static readonly Regex SecretRefPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);
    private static readonly Regex EntitySetPattern = new("^[A-Za-z][A-Za-z0-9_]{0,99}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<ConfigIssue> Validate()
    {
        var issues = new List<ConfigIssue>();
        if (!ErpProviders.All.Contains(Provider)) issues.Add(new("provider_unknown", "Unknown ERP provider."));
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            issues.Add(new("base_url_invalid", "The base URL must be an https address."));
        }

        if (Provider == ErpProviders.D365Finance)
        {
            if (string.IsNullOrWhiteSpace(AuthorityTenantId) || AuthorityTenantId.Length > 100) issues.Add(new("authority_required", "The Entra directory (tenant) id is required."));
            if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 100) issues.Add(new("client_id_required", "The application (client) id is required."));
        }

        if (!SecretRefPattern.IsMatch(SecretRef ?? "")) issues.Add(new("secret_ref_invalid", "Give the name of the secret in the vault, not the secret itself."));
        foreach (var (role, name) in EntitySets)
        {
            if (!EntitySetRoles.Contains(role)) issues.Add(new("entity_set_role_unknown", $"Unknown entity set role '{role}'."));
            else if (!EntitySetPattern.IsMatch(name)) issues.Add(new("entity_set_name_invalid", $"'{name}' is not a valid entity set name."));
        }

        if (PageSize is < 10 or > 1000) issues.Add(new("page_size_invalid", "The page size must be between 10 and 1000."));
        if (TimeoutSeconds is < 5 or > 300) issues.Add(new("timeout_invalid", "The timeout must be between 5 and 300 seconds."));
        return issues;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ConnectionConfig Parse(string json) => JsonSerializer.Deserialize<ConnectionConfig>(json, Json)!;
}

public sealed record DimensionMap(string VpmsDimension, string ErpDimension, string? DefaultValue, IReadOnlyDictionary<string, string> ValueMap);

public sealed record MappingConfig(
    string Company, string JournalName, string? PostingProfile, IReadOnlyDictionary<string, string> PaymentTermsMap, IReadOnlyDictionary<string, string> TaxCodeMap,
    IReadOnlyList<DimensionMap> Dimensions)
{
    private static readonly Regex Token = new("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<ConfigIssue> Validate()
    {
        var issues = new List<ConfigIssue>();
        if (!Token.IsMatch(Company ?? "")) issues.Add(new("company_invalid", "Give the ERP company (data area) id."));
        if (!Token.IsMatch(JournalName ?? "")) issues.Add(new("journal_invalid", "Give the vendor invoice journal name."));
        if (PostingProfile is not null && !Token.IsMatch(PostingProfile)) issues.Add(new("posting_profile_invalid", "The posting profile is not valid."));
        foreach (var kv in PaymentTermsMap.Concat(TaxCodeMap))
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || !Token.IsMatch(kv.Value ?? "")) issues.Add(new("map_value_invalid", $"'{kv.Key}' maps to an invalid value."));
        }

        if (Dimensions.GroupBy(d => d.VpmsDimension, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) issues.Add(new("dimension_duplicate", "A dimension is mapped twice."));
        foreach (var d in Dimensions)
        {
            if (string.IsNullOrWhiteSpace(d.VpmsDimension) || !Token.IsMatch(d.ErpDimension ?? "")) issues.Add(new("dimension_invalid", $"The mapping for '{d.VpmsDimension}' is not valid."));
            if (d.DefaultValue is not null && !Token.IsMatch(d.DefaultValue)) issues.Add(new("dimension_default_invalid", $"The default for '{d.VpmsDimension}' is not valid."));
            if (d.ValueMap.Any(v => !Token.IsMatch(v.Value ?? ""))) issues.Add(new("dimension_value_invalid", $"A value for '{d.VpmsDimension}' maps to an invalid value."));
        }

        return issues;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static MappingConfig Parse(string json) => JsonSerializer.Deserialize<MappingConfig>(json, Json)!;
}
