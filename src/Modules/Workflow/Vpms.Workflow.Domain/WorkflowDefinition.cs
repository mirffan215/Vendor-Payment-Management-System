using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Workflow.Domain;

public static class DefinitionStatus
{
    public const string Active = "Active";
    public const string Pending = "Pending";
    public const string Superseded = "Superseded";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

public static class DefinitionSource
{
    /// <summary>Written directly as a workflow graph.</summary>
    public const string Authored = "Authored";

    /// <summary>Compiled from the legal entity's approval matrix rules.</summary>
    public const string Matrix = "Matrix";
}

public static class SubjectTypes
{
    public const string Invoice = "Invoice";
}

/// <summary>
/// One immutable version of a legal entity's approval workflow. Routing decides who can release money, so every new version
/// stays Pending until a different person approves it (maker-checker); only then does it replace the active version.
/// Runs that started on an older version keep running on it.
/// </summary>
public class WorkflowDefinition : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SubjectType { get; set; } = SubjectTypes.Invoice;
    public int VersionNo { get; set; }
    public string SpecJson { get; set; } = "{}";

    /// <summary>Hash of the spec; recorded on each run so the exact routing it started with can be proved.</summary>
    public string SpecHash { get; set; } = "";

    public string Source { get; set; } = DefinitionSource.Authored;
    public string Status { get; set; } = DefinitionStatus.Pending;
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
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "approve a workflow change");
        Status = DefinitionStatus.Active;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Reject(Guid checker, string comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "reject a workflow change");
        if (string.IsNullOrWhiteSpace(comment)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = DefinitionStatus.Rejected;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Cancel(Guid actor, DateTimeOffset now)
    {
        RequirePending();
        if (actor != CreatedBy) throw new ConflictException("not_the_maker", "Only the person who made the change can cancel it.");
        Status = DefinitionStatus.Cancelled;
        DecidedAt = now;
    }

    public void Supersede()
    {
        if (Status != DefinitionStatus.Active) throw new ConflictException("invalid_transition", "Only the active version can be superseded.");
        Status = DefinitionStatus.Superseded;
    }

    private void RequirePending()
    {
        if (Status != DefinitionStatus.Pending) throw new ConflictException("not_pending", "This change has already been decided.");
    }

    private static string? Clean(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)];
}
