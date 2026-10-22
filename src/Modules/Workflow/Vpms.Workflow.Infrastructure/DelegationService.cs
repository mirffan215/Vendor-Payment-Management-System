using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Identity.Contracts;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;

namespace Vpms.Workflow.Infrastructure;

public sealed class DelegationService(
    WorkflowDbContext db, ILegalEntityLookup entities, IApproverDirectory directory, IUserDirectory users, IAuditPort audit, ICurrentUser current, TimeProvider clock)
    : IDelegationService
{
    public async Task<IReadOnlyList<DelegationDto>> MineAsync(bool activeOnly, CancellationToken ct = default)
    {
        var me = current.UserId;
        var now = clock.GetUtcNow();
        var q = db.Delegations.AsNoTracking().Where(d => d.DelegatorId == me || d.DelegateId == me);
        if (activeOnly) q = q.Where(d => d.Status == DelegationStatus.Active && d.ValidTo > now);
        return await MapAsync(await q.OrderByDescending(d => d.CreatedAt).Take(200).ToListAsync(ct), ct);
    }

    public async Task<IReadOnlyList<DelegationDto>> ForEntityAsync(Guid legalEntityId, CancellationToken ct = default) =>
        await MapAsync(await db.Delegations.AsNoTracking().Where(d => d.LegalEntityId == legalEntityId).OrderByDescending(d => d.CreatedAt).Take(200).ToListAsync(ct), ct);

    public async Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default) =>
        await db.Delegations.AsNoTracking().Where(d => d.Id == id).Select(d => (Guid?)d.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Delegation");

    public async Task<DelegationDto> CreateAsync(CreateDelegation r, bool onBehalfOfOthersAllowed, CancellationToken ct = default)
    {
        var actor = current.UserId;
        var delegator = r.DelegatorId ?? actor;
        if (delegator != actor && !onBehalfOfOthersAllowed)
        {
            throw new ValidationFailedException("not_allowed", "You can only delegate your own approvals.", 403);
        }

        Delegation.ValidateWindow(delegator, r.DelegateId, r.ValidFrom, r.ValidTo, r.MaxAmount, r.Reason);
        var now = clock.GetUtcNow();
        if (r.ValidTo <= now) throw new ValidationFailedException("invalid_period", "The delegation must end in the future.");
        if (!await entities.ExistsAsync(r.LegalEntityId, ct)) throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist.");

        var permitted = await directory.FilterByPermissionAsync(r.LegalEntityId, Permissions.InvoiceApprove, [delegator, r.DelegateId], ct);
        if (!permitted.Contains(delegator) || !permitted.Contains(r.DelegateId))
        {
            throw new ValidationFailedException("approver_not_permitted", "Both people must hold the invoice-approval permission for this legal entity.");
        }

        if (await db.Delegations.AnyAsync(d => d.LegalEntityId == r.LegalEntityId && d.DelegatorId == delegator && d.DelegateId == r.DelegateId
                                               && d.Status == DelegationStatus.Active && d.ValidFrom < r.ValidTo && r.ValidFrom < d.ValidTo, ct))
        {
            throw new ConflictException("delegation_overlaps", "These two people already have a delegation for part of this period.");
        }

        var row = new Delegation
        {
            Id = Guid.NewGuid(), LegalEntityId = r.LegalEntityId, DelegatorId = delegator, DelegateId = r.DelegateId, ValidFrom = r.ValidFrom, ValidTo = r.ValidTo,
            MaxAmount = r.MaxAmount, Reason = r.Reason.Trim(), CreatedBy = actor, CreatedAt = now,
        };
        db.Delegations.Add(row);
        audit.Record(new AuditEntry("delegation.created", "Delegation", row.Id.ToString(), row.LegalEntityId,
            JsonSerializer.Serialize(new { row.DelegatorId, row.DelegateId, row.ValidFrom, row.ValidTo, row.MaxAmount })));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([row], ct)).Single();
    }

    public async Task<DelegationDto> RevokeAsync(Guid id, bool administrator, CancellationToken ct = default)
    {
        var row = await db.Delegations.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Delegation");
        var actor = current.UserId;
        if (!administrator && actor != row.DelegatorId && actor != row.DelegateId) throw new NotFoundException("Delegation");

        row.Revoke(actor, clock.GetUtcNow());
        audit.Record(new AuditEntry("delegation.revoked", "Delegation", id.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([row], ct)).Single();
    }

    private async Task<IReadOnlyList<DelegationDto>> MapAsync(IReadOnlyList<Delegation> rows, CancellationToken ct)
    {
        var names = await users.GetAsync(rows.SelectMany(r => new[] { r.DelegatorId, r.DelegateId }).Distinct(), ct);
        string? Name(Guid id) => names.TryGetValue(id, out var u) ? u.DisplayName : null;
        var now = clock.GetUtcNow();
        return rows.Select(r => new DelegationDto(r.Id, r.LegalEntityId, r.DelegatorId, Name(r.DelegatorId), r.DelegateId, Name(r.DelegateId), r.ValidFrom, r.ValidTo, r.MaxAmount,
            r.Status == DelegationStatus.Active && r.ValidTo <= now ? "Expired" : r.Status, r.Reason, r.CreatedAt, r.CreatedBy)).ToList();
    }
}
