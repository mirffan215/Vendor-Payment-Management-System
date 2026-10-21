using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Audit.Application;
using Vpms.Identity.Contracts;

namespace Vpms.Api.Controllers;

[ApiController]
[Route("api/v1/audit")]
public sealed class AuditController(IAuditQueryService query, IPermissionEvaluator permissions) : ControllerBase
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
}

public sealed record AuditEventsRequest(
    Guid? LegalEntityId, Guid? ActorId, string? Action, string? EntityType, string? EntityId, string? CorrelationId,
    DateTimeOffset? From, DateTimeOffset? To, long? AfterSequence, int? Limit);
