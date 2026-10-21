using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Contracts;
using Vpms.CustomerAdmin.Domain;

namespace Vpms.CustomerAdmin.Infrastructure;

public sealed class LegalEntityService(CustomerAdminDbContext db, IFieldProtector protector, IAuditPort audit, TimeProvider clock)
    : ILegalEntityService, ILegalEntityLookup
{
    private const string TaxColumn = "customer.LegalEntity.TaxRegistrationNo";

    public async Task<IReadOnlyList<LegalEntityDto>> ListAsync(IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var query = db.LegalEntities.AsNoTracking();
        if (entityScope is not null)
        {
            query = query.Where(e => entityScope.Contains(e.Id));
        }

        var rows = await query.OrderBy(e => e.Code).ToListAsync(ct);
        var result = new List<LegalEntityDto>();
        foreach (var row in rows)
        {
            result.Add(await MapAsync(row, ct));
        }

        return result;
    }

    public async Task<LegalEntityDto> GetAsync(Guid id, CancellationToken ct = default) =>
        await MapAsync(await db.LegalEntities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct)
                       ?? throw new NotFoundException("Legal entity"), ct);

    public async Task<LegalEntityDto> CreateAsync(CreateLegalEntity request, CancellationToken ct = default)
    {
        var code = (request.Code ?? "").Trim().ToUpperInvariant();
        Validate(code, request.LegalName, request.CountryCode, request.BaseCurrency);

        var entity = new LegalEntity
        {
            Id = Guid.NewGuid(), Code = code, LegalName = request.LegalName.Trim(), LegalNameAr = Trim(request.LegalNameAr),
            CountryCode = request.CountryCode, BaseCurrency = request.BaseCurrency, Status = "Active", CreatedAt = clock.GetUtcNow(),
        };
        await ApplyTaxNumberAsync(entity, request.TaxRegistrationNo, ct);
        db.LegalEntities.Add(entity);
        audit.Record(new AuditEntry("legal-entity.created", "LegalEntity", entity.Id.ToString(), entity.Id,
            JsonSerializer.Serialize(new { entity.Code, entity.LegalName, entity.CountryCode, entity.BaseCurrency, hasTaxNumber = entity.TaxRegistrationNoCipher is not null })));
        await SaveAsync(ct);
        return await MapAsync(entity, ct);
    }

    public async Task<LegalEntityDto> UpdateAsync(Guid id, UpdateLegalEntity request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.LegalName) || request.LegalName.Length > 300)
        {
            throw new ValidationFailedException("invalid_name", "The legal name is required (max 300 characters).");
        }

        var entity = await db.LegalEntities.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Legal entity");
        var taxChanged = request.TaxRegistrationNo is not null;
        entity.LegalName = request.LegalName.Trim();
        entity.LegalNameAr = Trim(request.LegalNameAr);
        if (taxChanged)
        {
            await ApplyTaxNumberAsync(entity, request.TaxRegistrationNo, ct);
        }

        audit.Record(new AuditEntry("legal-entity.updated", "LegalEntity", id.ToString(), id,
            JsonSerializer.Serialize(new { entity.LegalName, entity.LegalNameAr, taxNumberChanged = taxChanged })));
        await SaveAsync(ct);
        return await MapAsync(entity, ct);
    }

    public async Task<LegalEntityDto> SetStatusAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var entity = await db.LegalEntities.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Legal entity");
        var status = active ? "Active" : "Inactive";
        if (entity.Status != status)
        {
            audit.Record(new AuditEntry("legal-entity.status-changed", "LegalEntity", id.ToString(), id,
                JsonSerializer.Serialize(new { from = entity.Status, to = status })));
            entity.Status = status;
            await db.SaveChangesAsync(ct);
        }

        return await MapAsync(entity, ct);
    }

    public Task<bool> ExistsAsync(Guid legalEntityId, CancellationToken ct = default) =>
        db.LegalEntities.AnyAsync(e => e.Id == legalEntityId, ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IEnumerable<Guid> legalEntityIds, CancellationToken ct = default)
    {
        var ids = legalEntityIds.Distinct().ToList();
        return await db.LegalEntities.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.LegalName, ct);
    }

    private static void Validate(string code, string? name, string? country, string? currency)
    {
        if (!AdminValidation.Code.IsMatch(code))
        {
            throw new ValidationFailedException("invalid_code", "The code may contain A-Z, 0-9, '-' and '_' (max 50).");
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 300)
        {
            throw new ValidationFailedException("invalid_name", "The legal name is required (max 300 characters).");
        }

        if (country is null || !AdminValidation.Country.IsMatch(country))
        {
            throw new ValidationFailedException("invalid_country", "The country must be an ISO 3166 alpha-2 code, e.g. SA.");
        }

        if (currency is null || !AdminValidation.Currency.IsMatch(currency))
        {
            throw new ValidationFailedException("invalid_currency", "The currency must be an ISO 4217 code, e.g. SAR.");
        }
    }

    private async Task ApplyTaxNumberAsync(LegalEntity entity, string? taxNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taxNumber))
        {
            entity.TaxRegistrationNoCipher = null;
            entity.TaxRegistrationNoIndex = null;
            return;
        }

        var normalized = AdminValidation.NormalizeTaxNumber(taxNumber);
        if (normalized.Length is < 5 or > 30)
        {
            throw new ValidationFailedException("invalid_tax_number", "The tax registration number looks invalid.");
        }

        entity.TaxRegistrationNoCipher = await protector.ProtectAsync(TaxColumn, normalized, ct);
        entity.TaxRegistrationNoIndex = await protector.BlindIndexAsync(TaxColumn, normalized, ct);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } sql)
        {
            db.ChangeTracker.Clear();
            throw sql.Message.Contains("TaxRegistrationNoIndex", StringComparison.Ordinal)
                ? new ConflictException("tax_number_exists", "Another legal entity already uses this tax registration number.")
                : new ConflictException("entity_code_exists", "A legal entity with this code already exists.");
        }
    }

    private async Task<LegalEntityDto> MapAsync(LegalEntity e, CancellationToken ct)
    {
        string? masked = null;
        if (e.TaxRegistrationNoCipher is not null)
        {
            masked = AuditRedactor.MaskTail(await protector.UnprotectAsync(TaxColumn, e.TaxRegistrationNoCipher, ct), 3);
        }

        return new LegalEntityDto(e.Id, e.Code, e.LegalName, e.LegalNameAr, e.CountryCode, e.BaseCurrency, e.Status,
            e.TaxRegistrationNoCipher is not null, masked);
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
