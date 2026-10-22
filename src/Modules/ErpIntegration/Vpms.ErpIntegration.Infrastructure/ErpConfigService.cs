using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.Identity.Contracts;

namespace Vpms.ErpIntegration.Infrastructure;

public sealed class ErpConfigService(
    ErpDbContext db, ILegalEntityLookup entities, IAuditPort audit, ICurrentUser current, TimeProvider clock, ErpContextResolver contexts) : IErpConfigService
{
    public Task<ErpConfigStateDto> GetConnectionAsync(CancellationToken ct = default) => StateAsync(ConfigKind.Connection, null, ct);

    public async Task<ErpConfigStateDto> GetMappingAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        return await StateAsync(ConfigKind.Mapping, legalEntityId, ct);
    }

    public Task<ErpConfigDto> ProposeConnectionAsync(ConnectionConfig config, string note, CancellationToken ct = default)
    {
        var issues = config.Validate();
        if (issues.Count > 0) throw new ValidationFailedException(issues[0].Code, issues[0].Message);
        return ProposeAsync(ConfigKind.Connection, null, config.ToJson(), note, ct);
    }

    public async Task<ErpConfigDto> ProposeMappingAsync(Guid legalEntityId, MappingConfig config, string note, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        var issues = config.Validate();
        if (issues.Count > 0) throw new ValidationFailedException(issues[0].Code, issues[0].Message);
        return await ProposeAsync(ConfigKind.Mapping, legalEntityId, config.ToJson(), note, ct);
    }

    public async Task<IReadOnlyList<ErpConfigChangeDto>> ListChangesAsync(string? status, IReadOnlySet<Guid>? entityScope, bool includeTenantWide, CancellationToken ct = default)
    {
        var q = db.Configs.AsNoTracking().AsQueryable();
        q = string.IsNullOrWhiteSpace(status) ? q.Where(c => c.Status != ConfigStatus.Superseded) : q.Where(c => c.Status == status);
        if (entityScope is not null) q = q.Where(c => (c.LegalEntityId == null && includeTenantWide) || (c.LegalEntityId != null && entityScope.Contains(c.LegalEntityId.Value)));
        else if (!includeTenantWide) q = q.Where(c => c.LegalEntityId != null);

        var rows = await q.OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ct);
        var result = new List<ErpConfigChangeDto>();
        foreach (var r in rows)
        {
            var baseRow = await db.Configs.AsNoTracking().Where(b => b.Kind == r.Kind && b.LegalEntityId == r.LegalEntityId && b.VersionNo < r.VersionNo
                                                                    && (b.Status == ConfigStatus.Active || b.Status == ConfigStatus.Superseded))
                .OrderByDescending(b => b.VersionNo).FirstOrDefaultAsync(ct);
            result.Add(new ErpConfigChangeDto(Map(r), baseRow is null ? null : Map(baseRow)));
        }

        return result;
    }

    public async Task<Guid?> EntityOfChangeAsync(Guid changeId, CancellationToken ct = default)
    {
        var row = await db.Configs.AsNoTracking().Where(c => c.Id == changeId).Select(c => new { c.LegalEntityId }).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("ERP change");
        return row.LegalEntityId;
    }

    public async Task<ErpConfigDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Approve(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.erp-change.blocked", "ErpConfig", changeId.ToString(), row.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "approve", rule = ex.Rule })), ct);
            throw;
        }

        // Retire the old version first so the one-active-version index holds at every step.
        var active = await db.Configs.FirstOrDefaultAsync(c => c.Kind == row.Kind && c.LegalEntityId == row.LegalEntityId && c.Status == ConfigStatus.Active && c.Id != row.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        row.Status = ConfigStatus.Pending;
        if (active is not null)
        {
            active.Supersede();
            await db.SaveChangesAsync(ct);
        }

        row.Status = ConfigStatus.Active;
        audit.Record(new AuditEntry("erp.config.approved", "ErpConfig", changeId.ToString(), row.LegalEntityId,
            JsonSerializer.Serialize(new { kind = row.Kind, version = row.VersionNo })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task<ErpConfigDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Reject(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.erp-change.blocked", "ErpConfig", changeId.ToString(), row.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "reject", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("erp.config.rejected", "ErpConfig", changeId.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<ErpConfigDto> CancelAsync(Guid changeId, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        row.Cancel(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("erp.config.cancelled", "ErpConfig", changeId.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<ConnectionTestDto> TestAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        var resolved = await contexts.ResolveAsync(legalEntityId, ct);
        if (resolved.Error is not null) return new ConnectionTestDto(false, resolved.Error.Code, resolved.Error.Message);

        var result = await resolved.Adapter!.ValidateConnectionAsync(resolved.Context!, ct);
        audit.Record(new AuditEntry("erp.connection.tested", "ErpConfig", legalEntityId.ToString(), legalEntityId, JsonSerializer.Serialize(new { ok = result.IsSuccess, code = result.ErrorCode })));
        await db.SaveChangesAsync(ct);
        return new ConnectionTestDto(result.IsSuccess, result.ErrorCode, result.IsSuccess ? null : result.Message);
    }

    private async Task<ErpConfigDto> ProposeAsync(string kind, Guid? legalEntityId, string json, string note, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(note) || note.Length > 500) throw new ValidationFailedException("reason_required", "Say why the ERP configuration changes (max 500 characters).");
        if (await db.Configs.AnyAsync(c => c.Kind == kind && c.LegalEntityId == legalEntityId && c.Status == ConfigStatus.Pending, ct))
        {
            throw new ConflictException("change_pending", "There is already a change waiting for approval.");
        }

        var active = await db.Configs.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == kind && c.LegalEntityId == legalEntityId && c.Status == ConfigStatus.Active, ct);
        if (active is not null && active.ConfigJson == json) throw new ConflictException("no_change", "This is the configuration already in force.");

        var version = (await db.Configs.Where(c => c.Kind == kind && c.LegalEntityId == legalEntityId).MaxAsync(c => (int?)c.VersionNo, ct) ?? 0) + 1;
        var row = new ErpConfig
        {
            Id = Guid.NewGuid(), Kind = kind, LegalEntityId = legalEntityId, VersionNo = version, ConfigJson = json, Status = ConfigStatus.Pending, Note = note.Trim(),
            CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(),
        };

        try
        {
            db.Configs.Add(row);
            audit.Record(new AuditEntry("erp.config.proposed", "ErpConfig", row.Id.ToString(), legalEntityId, JsonSerializer.Serialize(new { kind, version })));
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("concurrent_change", "The configuration was changed at the same time. Reload and try again.");
        }

        return Map(row);
    }

    private async Task<ErpConfigStateDto> StateAsync(string kind, Guid? legalEntityId, CancellationToken ct)
    {
        var active = await db.Configs.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == kind && c.LegalEntityId == legalEntityId && c.Status == ConfigStatus.Active, ct);
        var pending = await db.Configs.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == kind && c.LegalEntityId == legalEntityId && c.Status == ConfigStatus.Pending, ct);
        return new ErpConfigStateDto(active is null ? null : Map(active), pending is null ? null : Map(pending));
    }

    private async Task<ErpConfig> Load(Guid id, CancellationToken ct) => await db.Configs.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("ERP change");

    private async Task EnsureEntity(Guid legalEntityId, CancellationToken ct)
    {
        if (!await entities.ExistsAsync(legalEntityId, ct)) throw new NotFoundException("Legal entity");
    }

    internal static ErpConfigDto Map(ErpConfig c) => new(
        c.Id, c.Kind, c.LegalEntityId, c.VersionNo, c.Status,
        c.Kind == ConfigKind.Connection ? ConnectionConfig.Parse(c.ConfigJson) : null,
        c.Kind == ConfigKind.Mapping ? MappingConfig.Parse(c.ConfigJson) : null,
        c.Note, c.CreatedBy, c.CreatedAt, c.DecidedBy, c.DecidedAt, c.DecisionComment);
}

public sealed record ResolvedErp(IErpAdapter? Adapter, ErpContext? Context, ConfigIssue? Error);

/// <summary>Turns the active connection and entity mapping into an adapter and context, reading the secret only now and only in memory.</summary>
public sealed class ErpContextResolver(ErpDbContext db, ISecretReader secrets, IEnumerable<IErpAdapter> adapters)
{
    public async Task<ResolvedErp> ResolveAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        var conn = await db.Configs.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == ConfigKind.Connection && c.LegalEntityId == null && c.Status == ConfigStatus.Active, ct);
        if (conn is null) return Fail("connection_missing", "No approved ERP connection is configured.");
        var map = await db.Configs.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == ConfigKind.Mapping && c.LegalEntityId == legalEntityId && c.Status == ConfigStatus.Active, ct);
        if (map is null) return Fail("mapping_missing", "This legal entity has no approved ERP mapping.");

        var connection = ConnectionConfig.Parse(conn.ConfigJson);
        var adapter = adapters.FirstOrDefault(a => a.Provider == connection.Provider);
        if (adapter is null) return Fail("provider_unavailable", "The configured ERP provider is not available.");

        var secret = await secrets.ReadAsync(connection.SecretRef, ct);
        if (string.IsNullOrEmpty(secret)) return Fail("secret_unavailable", "The ERP secret could not be read from the vault.");

        var m = MappingConfig.Parse(map.ConfigJson);
        var settings = new ErpConnectionSettings(connection.BaseUrl, connection.AuthorityTenantId, connection.ClientId, secret, connection.EntitySets, connection.PageSize, connection.TimeoutSeconds);
        var company = new ErpCompanyMapping(m.Company, m.JournalName, m.PostingProfile, m.PaymentTermsMap, m.TaxCodeMap,
            m.Dimensions.Select(d => new DimensionMapping(d.VpmsDimension, d.ErpDimension, d.DefaultValue, d.ValueMap)).ToList());
        return new ResolvedErp(adapter, new ErpContext(settings, company), null);
    }

    private static ResolvedErp Fail(string code, string message) => new(null, null, new ConfigIssue(code, message));
}
