using Vpms.BuildingBlocks.Jobs;
using Vpms.CustomerAdmin.Domain;

namespace Vpms.CustomerAdmin.Application;

public sealed record RetentionPolicyDto(
    Guid? Id, int VersionNo, string Status, RetentionSettings Settings, string Note, Guid? CreatedBy, DateTimeOffset? CreatedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment);

public sealed record RetentionStateDto(RetentionPolicyDto? Active, RetentionPolicyDto? Pending, IReadOnlyDictionary<string, int> Defaults, IReadOnlyList<string> DisposedAutomatically);

public interface IRetentionPolicyService
{
    Task<RetentionStateDto> GetAsync(CancellationToken ct = default);

    Task<RetentionPolicyDto> ProposeAsync(RetentionSettings settings, string note, CancellationToken ct = default);

    Task<RetentionPolicyDto> ApproveAsync(Guid id, string? comment, CancellationToken ct = default);

    Task<RetentionPolicyDto> RejectAsync(Guid id, string comment, CancellationToken ct = default);

    Task<RetentionPolicyDto> CancelAsync(Guid id, CancellationToken ct = default);
}

public sealed record LegalHoldDto(
    Guid Id, string Scope, Guid? LegalEntityId, Guid? VendorId, string Reason, Guid PlacedBy, DateTimeOffset PlacedAt, Guid? ReleasedBy, DateTimeOffset? ReleasedAt, string? ReleaseComment);

public interface ILegalHoldService
{
    Task<IReadOnlyList<LegalHoldDto>> ListAsync(bool activeOnly, CancellationToken ct = default);

    Task<LegalHoldDto> PlaceAsync(string scope, Guid? legalEntityId, Guid? vendorId, string reason, CancellationToken ct = default);

    Task<LegalHoldDto> ReleaseAsync(Guid id, string comment, CancellationToken ct = default);
}

/// <summary>One line of the retention report: what is kept how long, and how much has passed that period.</summary>
public sealed record RetentionReportRow(string DataClass, int Months, DateTimeOffset Cutoff, bool DisposedAutomatically, int? EligibleCount, bool OnHold);

public interface IRetentionReportService
{
    Task<IReadOnlyList<RetentionReportRow>> BuildAsync(CancellationToken ct = default);
}
