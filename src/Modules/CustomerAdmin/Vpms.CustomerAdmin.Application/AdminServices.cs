namespace Vpms.CustomerAdmin.Application;

public sealed record TenantProfile(Guid Id, string Name, string Slug, string DefaultLocale, string DefaultCalendar, string? PrimaryColor, Guid? LogoAttachmentId);

public sealed record UpdateTenantProfile(string Name, string DefaultLocale, string DefaultCalendar, string? PrimaryColor, Guid? LogoAttachmentId);

public interface ITenantProfileService
{
    Task<TenantProfile> GetAsync(CancellationToken ct = default);

    Task<TenantProfile> UpdateAsync(UpdateTenantProfile update, CancellationToken ct = default);
}

public sealed record LegalEntityDto(
    Guid Id, string Code, string LegalName, string? LegalNameAr, string CountryCode, string BaseCurrency, string Status,
    bool HasTaxRegistrationNo, string? TaxRegistrationNoMasked);

public sealed record CreateLegalEntity(string Code, string LegalName, string? LegalNameAr, string CountryCode, string BaseCurrency, string? TaxRegistrationNo);

public sealed record UpdateLegalEntity(string LegalName, string? LegalNameAr, string? TaxRegistrationNo);

public interface ILegalEntityService
{
    /// <param name="entityScope">null = all entities of the tenant; otherwise only these.</param>
    Task<IReadOnlyList<LegalEntityDto>> ListAsync(IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<LegalEntityDto> GetAsync(Guid id, CancellationToken ct = default);

    Task<LegalEntityDto> CreateAsync(CreateLegalEntity request, CancellationToken ct = default);

    Task<LegalEntityDto> UpdateAsync(Guid id, UpdateLegalEntity request, CancellationToken ct = default);

    Task<LegalEntityDto> SetStatusAsync(Guid id, bool active, CancellationToken ct = default);
}

public sealed record ApprovalAuthorityDto(
    Guid Id, Guid LegalEntityId, Guid UserId, decimal MaxAmount, string CurrencyCode, string? DimensionScopeJson,
    DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string Status);

public sealed record CreateApprovalAuthority(
    Guid LegalEntityId, Guid UserId, decimal MaxAmount, string CurrencyCode, string? DimensionScopeJson,
    DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);

public sealed record ApprovalMatrixRuleDto(
    Guid Id, Guid LegalEntityId, string Name, int Priority, string ConditionsJson, string ApproverResolutionJson, int Levels, string Status);

public sealed record CreateApprovalMatrixRule(
    Guid LegalEntityId, string Name, int Priority, string ConditionsJson, string ApproverResolutionJson, int Levels);

public interface IApprovalAdminService
{
    Task<IReadOnlyList<ApprovalAuthorityDto>> ListAuthoritiesAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<ApprovalAuthorityDto> CreateAuthorityAsync(CreateApprovalAuthority request, CancellationToken ct = default);

    Task RevokeAuthorityAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ApprovalMatrixRuleDto>> ListRulesAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<ApprovalMatrixRuleDto> CreateRuleAsync(CreateApprovalMatrixRule request, CancellationToken ct = default);

    Task DisableRuleAsync(Guid id, CancellationToken ct = default);
}

public static class AdminValidation
{
    public static readonly System.Text.RegularExpressions.Regex Code = new("^[A-Z0-9][A-Z0-9_-]{0,49}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static readonly System.Text.RegularExpressions.Regex Country = new("^[A-Z]{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static readonly System.Text.RegularExpressions.Regex Currency = new("^[A-Z]{3}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static readonly System.Text.RegularExpressions.Regex Color = new("^#[0-9A-Fa-f]{6}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Tax registration numbers are compared without spaces, dashes or case.</summary>
    public static string NormalizeTaxNumber(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray().Select(char.ToUpperInvariant).ToArray());
}
