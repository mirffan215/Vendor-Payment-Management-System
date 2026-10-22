using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Jobs;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Invoices.Domain;

namespace Vpms.Invoices.Infrastructure;

/// <summary>
/// Financial records that have passed their retention period: decided invoices (approved, rejected or cancelled) dated before the cut-off of their
/// legal entity's country. Counted for the retention report; never deleted by the system in v1 (ADR-0024).
/// </summary>
public sealed class InvoiceRetentionTarget(InvoicesDbContext db, ILegalEntityLookup entities) : IRetentionTarget
{
    private static readonly string[] Decided = [InvoiceStatus.Approved, InvoiceStatus.Rejected, InvoiceStatus.Cancelled];

    public string DataClass => RetentionClasses.FinancialRecords;

    public bool CanDispose => false;

    public async Task<int> CountEligibleAsync(RetentionContext context, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var entityId in await db.Invoices.AsNoTracking().Select(i => i.LegalEntityId).Distinct().ToListAsync(ct))
        {
            if (context.TenantHeld || context.HeldEntities.Contains(entityId)) continue;
            var cutoff = DateOnly.FromDateTime(context.CutoffFor(DataClass, await entities.GetCountryAsync(entityId, ct)).UtcDateTime);
            var heldVendors = context.HeldVendors.ToList();
            total += await db.Invoices.AsNoTracking().CountAsync(i => i.LegalEntityId == entityId && Decided.Contains(i.Status) && i.InvoiceDate < cutoff && !heldVendors.Contains(i.VendorId), ct);
        }

        return total;
    }

    public Task<int> DisposeAsync(RetentionContext context, int batch, CancellationToken ct = default) =>
        throw new NotSupportedException("Financial records are not disposed of automatically.");
}
