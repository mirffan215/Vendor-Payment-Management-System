using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Domain.Verification;

namespace Vpms.Invoices.Infrastructure;

public sealed class InvoiceExceptionService(
    InvoicesDbContext db, IInvoiceVerifier verifier, IPermissionEvaluator permissions, IUserDirectory users, IAuditPort audit, ICurrentUser current, TimeProvider clock)
    : IInvoiceExceptionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ExceptionDto>> ListAsync(string? status, Guid? assignedTo, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default)
    {
        var q = db.Exceptions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(e => e.Status == status);
        if (assignedTo is { } a) q = q.Where(e => e.AssignedTo == a);
        if (entityScope is not null) q = q.Where(e => entityScope.Contains(e.LegalEntityId));
        var rows = await q.OrderByDescending(e => e.Severity).ThenBy(e => e.CreatedAt).ThenBy(e => e.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<Guid> EntityOfAsync(Guid exceptionId, CancellationToken ct = default) =>
        await db.Exceptions.AsNoTracking().Where(e => e.Id == exceptionId).Select(e => (Guid?)e.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Exception");

    public async Task<VerificationDetailDto> DetailAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice");
        var lastRun = await db.VerificationResults.AsNoTracking().Where(r => r.InvoiceId == invoiceId).OrderByDescending(r => r.CreatedAt).Select(r => (Guid?)r.RunId).FirstOrDefaultAsync(ct);
        var results = lastRun is null
            ? []
            : await db.VerificationResults.AsNoTracking().Where(r => r.RunId == lastRun).ToListAsync(ct);
        var exceptions = await db.Exceptions.AsNoTracking().Where(e => e.InvoiceId == invoiceId).OrderBy(e => e.CreatedAt).ToListAsync(ct);

        var order = RuleCatalog.All.Select((r, i) => (r.Code, i)).ToDictionary(x => x.Code, x => x.i);
        return new VerificationDetailDto(invoiceId, invoice.Status, results.FirstOrDefault()?.InvoiceVersion ?? invoice.Version, results.FirstOrDefault()?.RuleSetVersion ?? 0,
            results.FirstOrDefault()?.CreatedAt,
            results.OrderBy(r => order.GetValueOrDefault(r.RuleCode, 99)).Select(r => new VerificationResultDto(r.RuleCode, r.Outcome, r.Severity, Findings(r.DetailJson))).ToList(),
            await MapAsync(exceptions, ct));
    }

    public async Task<ExceptionDto> WaiveAsync(Guid exceptionId, string reason, CancellationToken ct = default)
    {
        var exception = await db.Exceptions.FirstOrDefaultAsync(e => e.Id == exceptionId, ct) ?? throw new NotFoundException("Exception");
        var invoice = await db.Invoices.FirstAsync(i => i.Id == exception.InvoiceId, ct);
        if (exception.Status != ExceptionStatus.Open) throw new ConflictException("invalid_transition", "This exception is already closed.");
        if (exception.InvoiceVersion != invoice.Version) throw new ConflictException("stale_exception", "The invoice changed after this exception was raised. Verify it again.");
        if (exception.RuleCode == RuleCodes.VendorState) throw new ConflictException("not_waivable", "A vendor that may not be transacted with cannot be waived through.");

        var me = current.UserId;
        if (exception.Severity == RuleSeverity.Block)
        {
            if (!await permissions.HasAsync(me, Permissions.InvoiceExceptionOverride, exception.LegalEntityId, ct))
            {
                throw new ConflictException("override_required", "Waiving a blocking check needs the override permission.");
            }

            try
            {
                var makers = new List<PriorActor> { new("creator of the invoice", invoice.CreatedBy) };
                if (invoice.LastEditedBy is { } editor) makers.Add(new("person who last edited the invoice", editor));
                DifferentPersonRule.Ensure(me, "waive a blocking check on this invoice", makers);
            }
            catch (SegregationOfDutiesException ex)
            {
                await audit.WriteNowAsync(new AuditEntry("sod.invoice-exception.blocked", "InvoiceException", exceptionId.ToString(), exception.LegalEntityId,
                    JsonSerializer.Serialize(new { attempted = "waive", rule = ex.Rule, exception.RuleCode })), ct);
                throw;
            }
        }

        exception.Waive(me, reason, clock.GetUtcNow());
        var openLeft = await db.Exceptions.AnyAsync(e => e.InvoiceId == invoice.Id && e.Status == ExceptionStatus.Open && e.Id != exceptionId, ct);
        if (!openLeft && invoice.Status == InvoiceStatus.Exception) invoice.ClearExceptions();

        audit.Record(new AuditEntry("invoice.exception.waived", "InvoiceException", exceptionId.ToString(), exception.LegalEntityId,
            JsonSerializer.Serialize(new { exception.InvoiceId, exception.RuleCode, exception.Severity, invoiceStatus = invoice.Status })));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([exception], ct)).Single();
    }

    public async Task<ExceptionDto> AssignAsync(Guid exceptionId, Guid? userId, CancellationToken ct = default)
    {
        var exception = await db.Exceptions.FirstOrDefaultAsync(e => e.Id == exceptionId, ct) ?? throw new NotFoundException("Exception");
        if (exception.Status != ExceptionStatus.Open) throw new ConflictException("invalid_transition", "This exception is already closed.");
        if (userId is { } id && !await users.ExistsAsync(id, ct)) throw new ValidationFailedException("unknown_user", "That user does not exist.");

        exception.AssignedTo = userId;
        audit.Record(new AuditEntry("invoice.exception.assigned", "InvoiceException", exceptionId.ToString(), exception.LegalEntityId,
            JsonSerializer.Serialize(new { assignedTo = userId })));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([exception], ct)).Single();
    }

    public async Task<VerificationDetailDto> ReverifyAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice");
        invoice.RequestReverification();
        audit.Record(new AuditEntry("invoice.reverification.requested", "Invoice", invoiceId.ToString(), invoice.LegalEntityId));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await verifier.VerifyAsync(invoiceId, ct);
        return await DetailAsync(invoiceId, ct);
    }

    // --- mapping -------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<ExceptionDto>> MapAsync(IReadOnlyList<InvoiceException> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.InvoiceId).Distinct().ToList();
        var numbers = await db.Invoices.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.InvoiceNumber, ct);
        return rows.Select(e => new ExceptionDto(e.Id, e.InvoiceId, numbers.GetValueOrDefault(e.InvoiceId, ""), e.LegalEntityId, e.VendorId, e.RuleCode, e.Severity, e.Status,
            Findings(e.DetailJson), e.InvoiceVersion, e.AssignedTo, e.ResolvedBy, e.ResolvedAt, e.Resolution, e.CreatedAt)).ToList();
    }

    private static IReadOnlyList<FindingDto> Findings(string json) => JsonSerializer.Deserialize<List<FindingDto>>(json, Json) ?? [];
}
