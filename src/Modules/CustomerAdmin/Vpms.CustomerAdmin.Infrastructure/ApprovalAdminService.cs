using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Domain;
using Vpms.Identity.Contracts;

namespace Vpms.CustomerAdmin.Infrastructure;

public sealed class ApprovalAdminService(
    CustomerAdminDbContext db, IUserDirectory users, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IApprovalAdminService
{
    private const int MaxJson = 8_000;

    public async Task<IReadOnlyList<ApprovalAuthorityDto>> ListAuthoritiesAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var q = db.ApprovalAuthorities.AsNoTracking().AsQueryable();
        if (entityScope is not null) q = q.Where(a => entityScope.Contains(a.LegalEntityId));
        if (legalEntityId is { } id) q = q.Where(a => a.LegalEntityId == id);
        return (await q.OrderBy(a => a.LegalEntityId).ThenBy(a => a.UserId).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<ApprovalAuthorityDto> CreateAuthorityAsync(CreateApprovalAuthority r, CancellationToken ct = default)
    {
        if (r.MaxAmount <= 0 || r.MaxAmount > 1_000_000_000_000m)
        {
            throw new ValidationFailedException("invalid_amount", "The limit must be greater than zero.");
        }

        if (!AdminValidation.Currency.IsMatch(r.CurrencyCode ?? ""))
        {
            throw new ValidationFailedException("invalid_currency", "The currency must be an ISO 4217 code, e.g. SAR.");
        }

        if (r.ValidTo is { } to && to <= r.ValidFrom)
        {
            throw new ValidationFailedException("invalid_period", "The end of validity must be after its start.");
        }

        RequireJson(r.DimensionScopeJson, "dimension scope", allowNull: true);
        await RequireEntityAndUser(r.LegalEntityId, r.UserId, ct);

        var overlapping = await db.ApprovalAuthorities.AnyAsync(a =>
            a.LegalEntityId == r.LegalEntityId && a.UserId == r.UserId && a.CurrencyCode == r.CurrencyCode && a.Status == "Active"
            && (a.ValidTo == null || a.ValidTo > r.ValidFrom) && (r.ValidTo == null || a.ValidFrom < r.ValidTo), ct);
        if (overlapping)
        {
            throw new ConflictException("authority_overlaps", "This person already has an authority in this currency for that period.");
        }

        var row = new ApprovalAuthority
        {
            Id = Guid.NewGuid(), LegalEntityId = r.LegalEntityId, UserId = r.UserId, MaxAmount = r.MaxAmount, CurrencyCode = r.CurrencyCode!,
            DimensionScopeJson = r.DimensionScopeJson, ValidFrom = r.ValidFrom, ValidTo = r.ValidTo, Status = "Active",
            CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(),
        };
        db.ApprovalAuthorities.Add(row);
        audit.Record(new AuditEntry("approval-authority.created", "ApprovalAuthority", row.Id.ToString(), row.LegalEntityId,
            JsonSerializer.Serialize(new { row.UserId, row.MaxAmount, row.CurrencyCode, row.ValidFrom, row.ValidTo })));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task RevokeAuthorityAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.ApprovalAuthorities.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Approval authority");
        if (row.Status == "Revoked")
        {
            return;
        }

        row.Status = "Revoked";
        row.ValidTo = clock.GetUtcNow();
        audit.Record(new AuditEntry("approval-authority.revoked", "ApprovalAuthority", id.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApprovalMatrixRuleDto>> ListRulesAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var q = db.ApprovalMatrixRules.AsNoTracking().AsQueryable();
        if (entityScope is not null) q = q.Where(a => entityScope.Contains(a.LegalEntityId));
        if (legalEntityId is { } id) q = q.Where(a => a.LegalEntityId == id);
        return (await q.OrderBy(a => a.LegalEntityId).ThenBy(a => a.Priority).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<ApprovalMatrixRuleDto> CreateRuleAsync(CreateApprovalMatrixRule r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200)
        {
            throw new ValidationFailedException("invalid_name", "The rule name is required (max 200 characters).");
        }

        if (r.Levels is < 1 or > 10 || r.Priority is < 0 or > 10_000)
        {
            throw new ValidationFailedException("invalid_rule", "Levels must be 1-10 and priority 0-10000.");
        }

        RequireJson(r.ConditionsJson, "conditions", allowNull: false);
        RequireJson(r.ApproverResolutionJson, "approver resolution", allowNull: false);
        await RequireEntityAndUser(r.LegalEntityId, null, ct);

        var row = new ApprovalMatrixRule
        {
            Id = Guid.NewGuid(), LegalEntityId = r.LegalEntityId, Name = r.Name.Trim(), Priority = r.Priority,
            ConditionsJson = r.ConditionsJson, ApproverResolutionJson = r.ApproverResolutionJson, Levels = r.Levels,
            Status = "Active", CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(),
        };
        db.ApprovalMatrixRules.Add(row);
        audit.Record(new AuditEntry("approval-rule.created", "ApprovalMatrixRule", row.Id.ToString(), row.LegalEntityId,
            JsonSerializer.Serialize(new { row.Name, row.Priority, row.Levels })));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task DisableRuleAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.ApprovalMatrixRules.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Approval rule");
        if (row.Status == "Disabled")
        {
            return;
        }

        row.Status = "Disabled";
        audit.Record(new AuditEntry("approval-rule.disabled", "ApprovalMatrixRule", id.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
    }

    private async Task RequireEntityAndUser(Guid legalEntityId, Guid? userId, CancellationToken ct)
    {
        if (!await db.LegalEntities.AnyAsync(e => e.Id == legalEntityId && e.Status == "Active", ct))
        {
            throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist or is inactive.");
        }

        if (userId is { } u && !await users.ExistsAsync(u, ct))
        {
            throw new ValidationFailedException("unknown_user", "The user does not exist.");
        }
    }

    private static void RequireJson(string? json, string what, bool allowNull)
    {
        if (json is null)
        {
            if (allowNull) return;
            throw new ValidationFailedException("invalid_json", $"The {what} are required.");
        }

        if (json.Length > MaxJson)
        {
            throw new ValidationFailedException("invalid_json", $"The {what} are too large.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ValidationFailedException("invalid_json", $"The {what} must be a JSON object.");
            }
        }
        catch (JsonException)
        {
            throw new ValidationFailedException("invalid_json", $"The {what} are not valid JSON.");
        }
    }

    private static ApprovalAuthorityDto Map(ApprovalAuthority a) => new(a.Id, a.LegalEntityId, a.UserId, a.MaxAmount, a.CurrencyCode,
        a.DimensionScopeJson, a.ValidFrom, a.ValidTo, a.Status);

    private static ApprovalMatrixRuleDto Map(ApprovalMatrixRule a) => new(a.Id, a.LegalEntityId, a.Name, a.Priority,
        a.ConditionsJson, a.ApproverResolutionJson, a.Levels, a.Status);
}
