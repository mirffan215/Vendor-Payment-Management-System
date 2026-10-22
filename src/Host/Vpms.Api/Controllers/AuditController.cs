using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Audit.Application;
using Vpms.Identity.Contracts;

namespace Vpms.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit")]
public sealed class AuditController(IAuditQueryService query, IAuditEvidenceService evidence, IPermissionEvaluator permissions) : ControllerBase
{
    /// <summary>Audit trail of the caller's tenant, limited to the legal entities the caller may read.</summary>
    [HttpGet("events")]
    public async Task<IActionResult> Events([FromQuery] AuditEventsRequest request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst(VpmsClaims.UserId)!.Value);
        var scope = await permissions.GetScopeAsync(userId, Permissions.AuditRead, ct);
        if (scope == PermissionScope.None)
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing permission Audit.Read.");
        }

        if (request.LegalEntityId is { } entity && !scope.Allows(entity))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "No access to this legal entity.");
        }

        var page = await query.QueryAsync(
            new AuditQuery(request.LegalEntityId, request.ActorId, request.Action, request.EntityType, request.EntityId,
                request.CorrelationId, request.From, request.To, request.AfterSequence, request.Limit ?? 50),
            scope.AllEntities ? null : scope.EntityIds, ct);

        return Ok(new
        {
            items = page.Items.Select(e => new
            {
                e.Sequence, e.Id, e.OccurredAt, e.LegalEntityId, e.ActorType, e.ActorId, e.OnBehalfOfId, e.Action,
                e.EntityType, e.EntityId, e.CorrelationId, e.IpAddress, e.BeforeHash, e.AfterHash,
                changeSummary = e.ChangeSummaryJson, e.PrevHash, e.Hash,
            }),
            nextAfterSequence = page.NextAfterSequence,
        });
    }

    /// <summary>A signed evidence bundle (zip) of the events matching the filter, with the state of the hash chain and its anchors. Needs Audit.Export.</summary>
    [HttpGet("evidence")]
    public async Task<IActionResult> Evidence([FromQuery] AuditEventsRequest request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst(VpmsClaims.UserId)!.Value);
        var scope = await permissions.GetScopeAsync(userId, Permissions.AuditExport, ct);
        if (scope == PermissionScope.None) return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing permission Audit.Export.");
        if (request.LegalEntityId is { } entity && !scope.Allows(entity)) return Problem(statusCode: StatusCodes.Status403Forbidden, title: "No access to this legal entity.");

        var bundle = await evidence.BuildAsync(
            new AuditQuery(request.LegalEntityId, request.ActorId, request.Action, request.EntityType, request.EntityId, request.CorrelationId, request.From, request.To, request.AfterSequence),
            scope.AllEntities ? null : scope.EntityIds, ct);
        Response.Headers["X-Manifest-SHA256"] = bundle.ManifestSha256;
        return File(bundle.Content, "application/zip", bundle.FileName);
    }

    /// <summary>Checks a bundle that was produced for this organisation: signature, file hashes, event hashes and links.</summary>
    [HttpPost("evidence/verify")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> VerifyEvidence(IFormFile file, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst(VpmsClaims.UserId)!.Value);
        if ((await permissions.GetScopeAsync(userId, Permissions.AuditExport, ct)) == PermissionScope.None) return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing permission Audit.Export.");
        await using var stream = file.OpenReadStream();
        var result = await evidence.VerifyAsync(stream, ct);
        return Ok(new AuditEvidenceVerifyResponse(result.IsValid, result.EventsChecked, result.SignatureValid, result.FilesIntact, result.EventsIntact, result.ChainLinked, result.Problems));
    }
}

public sealed record AuditEvidenceVerifyResponse(bool IsValid, int EventsChecked, bool SignatureValid, bool FilesIntact, bool EventsIntact, bool ChainLinked, IReadOnlyList<string> Problems);

public sealed record AuditEventsRequest(
    Guid? LegalEntityId, Guid? ActorId, string? Action, string? EntityType, string? EntityId, string? CorrelationId,
    DateTimeOffset? From, DateTimeOffset? To, long? AfterSequence, int? Limit);
