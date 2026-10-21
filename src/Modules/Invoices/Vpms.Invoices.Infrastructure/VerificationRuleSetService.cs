using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain.Verification;

namespace Vpms.Invoices.Infrastructure;

public sealed class VerificationRuleSetService(
    InvoicesDbContext db, ILegalEntityLookup entities, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IVerificationRuleSetService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EntityRulesDto> GetAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        var active = await ActiveRow(legalEntityId, ct);
        var pending = await db.RuleSets.AsNoTracking().FirstOrDefaultAsync(r => r.LegalEntityId == legalEntityId && r.Status == RuleSetStatus.Pending, ct);
        return new EntityRulesDto(active is null ? Default(legalEntityId) : Map(active), pending is null ? null : Map(pending));
    }

    public async Task<(int VersionNo, RuleConfig Config)> ActiveConfigAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        var active = await ActiveRow(legalEntityId, ct);
        return active is null ? (0, RuleConfig.Defaults()) : (active.VersionNo, Parse(active.ConfigJson));
    }

    public async Task<IReadOnlyList<RuleSetDto>> HistoryAsync(Guid legalEntityId, CancellationToken ct = default) =>
        (await db.RuleSets.AsNoTracking().Where(r => r.LegalEntityId == legalEntityId).OrderByDescending(r => r.VersionNo).ToListAsync(ct)).Select(Map).ToList();

    public async Task<RuleSetDto> ProposeAsync(Guid legalEntityId, ProposeRuleChange request, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 500) throw new ValidationFailedException("reason_required", "Say why the rules change (max 500 characters).");
        var next = RuleConfig.Normalize(request.Rules);

        if (await db.RuleSets.AnyAsync(r => r.LegalEntityId == legalEntityId && r.Status == RuleSetStatus.Pending, ct))
        {
            throw new ConflictException("change_pending", "There is already a change waiting for approval for this legal entity.");
        }

        var active = await ActiveRow(legalEntityId, ct);
        var baseConfig = active is null ? RuleConfig.Defaults() : Parse(active.ConfigJson);
        if (Serialize(next) == Serialize(baseConfig) && active is not null) throw new ConflictException("no_change", "These are the settings already in force.");

        var loosened = RuleConfig.LoosenedRules(baseConfig, next);
        var version = (await db.RuleSets.Where(r => r.LegalEntityId == legalEntityId).MaxAsync(r => (int?)r.VersionNo, ct) ?? 0) + 1;
        var row = new VerificationRuleSet
        {
            Id = Guid.NewGuid(), LegalEntityId = legalEntityId, VersionNo = version, ConfigJson = Serialize(next), Note = request.Note.Trim(),
            LoosenedRules = string.Join(',', loosened), CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(), Status = RuleSetStatus.Pending,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.RuleSets.Add(row);
            audit.Record(new AuditEntry("verification.rules.proposed", "VerificationRuleSet", row.Id.ToString(), legalEntityId,
                JsonSerializer.Serialize(new { version, loosened, needsApproval = loosened.Count > 0 })));
            await db.SaveChangesAsync(ct);

            if (loosened.Count == 0)
            {
                await ActivateAsync(row, active, ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("concurrent_change", "The rules were changed at the same time. Reload and try again.");
        }

        return Map(row);
    }

    public async Task<IReadOnlyList<RuleChangeDto>> ListChangesAsync(string? status, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var q = db.RuleSets.AsNoTracking().Where(r => r.VersionNo > 0 && r.LoosenedRules != "");
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        if (entityScope is not null) q = q.Where(r => entityScope.Contains(r.LegalEntityId));
        var rows = await q.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);

        var result = new List<RuleChangeDto>();
        foreach (var r in rows)
        {
            var baseRow = await db.RuleSets.AsNoTracking().Where(b => b.LegalEntityId == r.LegalEntityId && b.VersionNo < r.VersionNo && b.Status != RuleSetStatus.Pending
                                                                     && b.Status != RuleSetStatus.Rejected && b.Status != RuleSetStatus.Cancelled)
                .OrderByDescending(b => b.VersionNo).FirstOrDefaultAsync(ct);
            result.Add(new RuleChangeDto(Map(r), baseRow is null ? Default(r.LegalEntityId) : Map(baseRow)));
        }

        return result;
    }

    public async Task<Guid> EntityOfChangeAsync(Guid changeId, CancellationToken ct = default) =>
        await db.RuleSets.AsNoTracking().Where(r => r.Id == changeId).Select(r => (Guid?)r.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Rule change");

    public async Task<RuleSetDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Approve(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.verification-rules.blocked", "VerificationRuleSet", changeId.ToString(), row.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "approve", rule = ex.Rule })), ct);
            throw;
        }

        // Approve() already set this row to Active in memory; the old version is retired first so the one-active rule holds in the database.
        var active = await db.RuleSets.FirstOrDefaultAsync(r => r.LegalEntityId == row.LegalEntityId && r.Status == RuleSetStatus.Active && r.Id != row.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        row.Status = RuleSetStatus.Pending; // persist in two steps: retire the old version, then activate this one
        if (active is not null)
        {
            active.Supersede();
            await db.SaveChangesAsync(ct);
        }

        row.Status = RuleSetStatus.Active;
        audit.Record(new AuditEntry("verification.rules.approved", "VerificationRuleSet", changeId.ToString(), row.LegalEntityId,
            JsonSerializer.Serialize(new { version = row.VersionNo, loosened = row.LoosenedRules })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task<RuleSetDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Reject(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.verification-rules.blocked", "VerificationRuleSet", changeId.ToString(), row.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "reject", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("verification.rules.rejected", "VerificationRuleSet", changeId.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<RuleSetDto> CancelAsync(Guid changeId, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        row.Cancel(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("verification.rules.cancelled", "VerificationRuleSet", changeId.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    // --- internals -----------------------------------------------------------------------------------------

    private async Task ActivateAsync(VerificationRuleSet row, VerificationRuleSet? previous, CancellationToken ct)
    {
        if (previous is not null)
        {
            var tracked = await db.RuleSets.FirstAsync(r => r.Id == previous.Id, ct);
            tracked.Supersede();
            await db.SaveChangesAsync(ct);
        }

        row.ActivateImmediately(clock.GetUtcNow());
        audit.Record(new AuditEntry("verification.rules.activated", "VerificationRuleSet", row.Id.ToString(), row.LegalEntityId,
            JsonSerializer.Serialize(new { version = row.VersionNo })));
        await db.SaveChangesAsync(ct);
    }

    private Task<VerificationRuleSet?> ActiveRow(Guid legalEntityId, CancellationToken ct) =>
        db.RuleSets.AsNoTracking().FirstOrDefaultAsync(r => r.LegalEntityId == legalEntityId && r.Status == RuleSetStatus.Active, ct);

    private async Task<VerificationRuleSet> Load(Guid id, CancellationToken ct) =>
        await db.RuleSets.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Rule change");

    private async Task EnsureEntity(Guid legalEntityId, CancellationToken ct)
    {
        if (!await entities.ExistsAsync(legalEntityId, ct)) throw new NotFoundException("Legal entity");
    }

    private static RuleSetDto Default(Guid legalEntityId) =>
        new(null, legalEntityId, 0, "Default", RuleConfig.Defaults(), "Built-in defaults", [], null, null, null, null, null);

    private static RuleSetDto Map(VerificationRuleSet r) => new(r.Id, r.LegalEntityId, r.VersionNo, r.Status, Parse(r.ConfigJson), r.Note,
        r.LoosenedRules.Length == 0 ? [] : r.LoosenedRules.Split(','), r.CreatedBy, r.CreatedAt, r.DecidedBy, r.DecidedAt, r.DecisionComment);

    public static string Serialize(RuleConfig config) => JsonSerializer.Serialize(config.Rules, Json);

    public static RuleConfig Parse(string json) =>
        RuleConfig.Normalize(JsonSerializer.Deserialize<Dictionary<string, RuleSetting>>(json, Json));
}
