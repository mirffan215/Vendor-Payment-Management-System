using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Documents.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Vendors.Contracts;

namespace Vpms.Invoices.Infrastructure;

public sealed class InvoiceIntakeService(
    InvoicesDbContext db, InvoiceService invoices, InvoiceAttachmentService invoiceFiles, IAttachmentService attachments, IMailboxReader mailbox,
    IVendorLookup vendors, ILegalEntityLookup entities, IAuditPort audit, AuditContext auditContext, ICurrentUser current, TenantContext tenant,
    TimeProvider clock, ILogger<InvoiceIntakeService> logger) : IInvoiceIntakeService, IIntakeScan
{
    private const int BatchSize = 25;

    public async Task<InvoiceDto> CreateFromDocumentAsync(Guid legalEntityId, Guid vendorId, string fileName, Stream content, CancellationToken ct = default)
    {
        var draft = await invoices.CreateIntakeDraftAsync(legalEntityId, vendorId, InvoiceSource.Document, current.UserId, ActorKind.Staff, ct);
        try
        {
            var info = await attachments.UploadAsync(new UploadRequest(InvoiceAttachmentService.OwnerType, draft.Id.ToString(), legalEntityId, fileName, content, current.UserId, UploaderKind.Staff), ct);
            await invoiceFiles.AddCoreAsync(draft.Id, AttachmentRole.Original, info.Id, current.UserId, ActorKind.Staff, ct);
        }
        catch
        {
            await invoices.CancelAsync(draft.Id, "The uploaded file was refused.", CancellationToken.None);
            throw;
        }

        return await invoices.GetAsync(draft.Id, ct);
    }

    // --- email -------------------------------------------------------------------------------------------------

    public async Task<MailboxRunResult> ProcessMailboxAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        var box = await db.Mailboxes.FirstOrDefaultAsync(m => m.LegalEntityId == legalEntityId, ct);
        if (box is not { Enabled: true }) return new MailboxRunResult(0, 0, 0, 0, 0);

        auditContext.ActorType = AuditActorType.System;
        auditContext.ActorId = SystemActors.Intake;
        auditContext.CorrelationId ??= "intake-" + Guid.NewGuid().ToString("N")[..8];

        IReadOnlyList<InboundMail> messages;
        try
        {
            messages = await mailbox.FetchUnprocessedAsync(box.Address, BatchSize, ct);
            box.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the invoice mailbox of entity {Entity} failed.", legalEntityId);
            box.LastError = Trim(ex.Message, 500);
            box.LastPolledAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return new MailboxRunResult(0, 0, 0, 0, 1);
        }

        box.LastPolledAt = clock.GetUtcNow();
        int created = 0, unmatched = 0, skipped = 0, failed = 0;
        foreach (var mail in messages)
        {
            var existing = await db.IntakeMessages.FirstOrDefaultAsync(m => m.LegalEntityId == legalEntityId && m.ExternalMessageId == mail.Id, ct);
            if (existing is not null && IntakeStatus.IsFinal(existing.Status))
            {
                await mailbox.MarkProcessedAsync(box.Address, mail.Id, ct); // a replay: nothing new is created
                skipped++;
                continue;
            }

            var message = existing ?? new IntakeMessage
            {
                Id = Guid.NewGuid(), LegalEntityId = legalEntityId, ExternalMessageId = mail.Id, FromAddress = Trim(mail.From.Trim().ToLowerInvariant(), 320),
                Subject = Trim(mail.Subject, 300), ReceivedAt = mail.ReceivedAt, CreatedAt = clock.GetUtcNow(),
            };
            if (existing is null) db.IntakeMessages.Add(message);

            try
            {
                await HandleAsync(box, message, mail, ct);
                message.Attempts++; // failures are counted by RecordFailureAsync
                await db.SaveChangesAsync(ct);
                if (message.Status == IntakeStatus.Processed) created++;
                else unmatched++;
                await mailbox.MarkProcessedAsync(box.Address, mail.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex, "Processing mail {MessageId} failed (attempt {Attempt}).", mail.Id, message.Attempts);
                db.ChangeTracker.Clear();
                await RecordFailureAsync(legalEntityId, mail, ex, ct);
                if ((await db.IntakeMessages.AsNoTracking().FirstAsync(m => m.LegalEntityId == legalEntityId && m.ExternalMessageId == mail.Id, ct)).Status == IntakeStatus.DeadLetter)
                {
                    await mailbox.MarkProcessedAsync(box.Address, mail.Id, ct);
                }

                box = await db.Mailboxes.FirstAsync(m => m.LegalEntityId == legalEntityId, ct);
                box.LastPolledAt = clock.GetUtcNow();
            }
        }

        await db.SaveChangesAsync(ct);
        return new MailboxRunResult(messages.Count, created, unmatched, skipped, failed);
    }

    private async Task HandleAsync(InvoiceMailbox box, IntakeMessage message, InboundMail mail, CancellationToken ct)
    {
        // Persist the message row first: its id owns the stored attachments.
        await db.SaveChangesAsync(ct);

        var stored = message.AttachmentIds.Length == 0 ? [] : message.AttachmentIds.Split(',').Select(Guid.Parse).ToList();
        if (stored.Count == 0)
        {
            var notes = new List<string>();
            foreach (var file in mail.Attachments)
            {
                try
                {
                    var info = await attachments.UploadAsync(new UploadRequest(AttachmentServiceNames.IntakeOwner, message.Id.ToString(), message.LegalEntityId, file.FileName,
                        new MemoryStream(file.Content), SystemActors.Intake, UploaderKind.System), ct);
                    stored.Add(info.Id);
                }
                catch (ValidationFailedException ex)
                {
                    notes.Add($"{Trim(file.FileName, 60)}: {ex.Code}");
                }
            }

            message.AttachmentIds = string.Join(',', stored);
            if (notes.Count > 0) message.Note = Trim(string.Join("; ", notes), 500);
        }

        if (stored.Count == 0)
        {
            message.Status = IntakeStatus.Dismissed;
            message.Note = Trim("No usable attachment. " + message.Note, 500);
            message.ProcessedAt = clock.GetUtcNow();
            audit.Record(new AuditEntry("invoice.intake.dismissed", "IntakeMessage", message.Id.ToString(), message.LegalEntityId));
            await db.SaveChangesAsync(ct);
            return;
        }

        var matches = await vendors.FindActiveByContactEmailAsync(message.FromAddress, message.LegalEntityId, ct);
        if (matches.Count == 1)
        {
            await CreateDraftAsync(message, matches[0], SystemActors.Intake, ActorKind.System, ct);
            return;
        }

        message.Status = IntakeStatus.Unmatched;
        message.Note = matches.Count == 0 ? "The sender is not a known contact of any active vendor of this entity." : "The sender matches more than one vendor.";
        message.ProcessedAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("invoice.intake.unmatched", "IntakeMessage", message.Id.ToString(), message.LegalEntityId,
            JsonSerializer.Serialize(new { matches = matches.Count })));
        await db.SaveChangesAsync(ct);
    }

    private async Task CreateDraftAsync(IntakeMessage message, Guid vendorId, Guid actor, string actorKind, CancellationToken ct)
    {
        var draft = await invoices.CreateIntakeDraftAsync(message.LegalEntityId, vendorId, InvoiceSource.Email, actor, actorKind, ct);
        var ids = message.AttachmentIds.Split(',').Select(Guid.Parse).ToList();
        for (var i = 0; i < ids.Count; i++)
        {
            await attachments.ReassignFromIntakeAsync(ids[i], InvoiceAttachmentService.OwnerType, draft.Id.ToString(), message.LegalEntityId, ct);
            await invoiceFiles.AddCoreAsync(draft.Id, i == 0 ? AttachmentRole.Original : AttachmentRole.Supporting, ids[i], actor, actorKind, ct);
        }

        message.VendorId = vendorId;
        message.InvoiceId = draft.Id;
        message.Status = IntakeStatus.Processed;
        message.ProcessedAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("invoice.intake.processed", "IntakeMessage", message.Id.ToString(), message.LegalEntityId, ActorId: actor,
            ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId, invoiceId = draft.Id, attachments = ids.Count })));
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordFailureAsync(Guid legalEntityId, InboundMail mail, Exception ex, CancellationToken ct)
    {
        var message = await db.IntakeMessages.FirstOrDefaultAsync(m => m.LegalEntityId == legalEntityId && m.ExternalMessageId == mail.Id, ct);
        if (message is null)
        {
            message = new IntakeMessage
            {
                Id = Guid.NewGuid(), LegalEntityId = legalEntityId, ExternalMessageId = mail.Id, FromAddress = Trim(mail.From.Trim().ToLowerInvariant(), 320),
                Subject = Trim(mail.Subject, 300), ReceivedAt = mail.ReceivedAt, CreatedAt = clock.GetUtcNow(),
            };
            db.IntakeMessages.Add(message);
        }

        message.Attempts++;
        message.LastError = Trim(ex.GetType().Name + ": " + ex.Message, 500);
        message.Status = message.Attempts >= IntakeMessage.MaxAttempts ? IntakeStatus.DeadLetter : IntakeStatus.Failed;
        if (message.Status == IntakeStatus.DeadLetter)
        {
            message.ProcessedAt = clock.GetUtcNow();
            audit.Record(new AuditEntry("invoice.intake.dead-lettered", "IntakeMessage", message.Id.ToString(), legalEntityId));
        }

        await db.SaveChangesAsync(ct);
    }

    // --- people deciding about mail ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<IntakeMessageDto>> ListMessagesAsync(string? status, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default)
    {
        var q = db.IntakeMessages.AsNoTracking().AsQueryable();
        if (entityScope is not null) q = q.Where(m => entityScope.Contains(m.LegalEntityId));
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(m => m.Status == status);
        return (await q.OrderByDescending(m => m.CreatedAt).ThenBy(m => m.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<Guid> EntityOfMessageAsync(Guid messageId, CancellationToken ct = default) =>
        await db.IntakeMessages.AsNoTracking().Where(m => m.Id == messageId).Select(m => (Guid?)m.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Message");

    public async Task<IntakeMessageDto> AssignAsync(Guid messageId, Guid vendorId, CancellationToken ct = default)
    {
        var message = await db.IntakeMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct) ?? throw new NotFoundException("Message");
        if (message.Status is not (IntakeStatus.Unmatched or IntakeStatus.Failed or IntakeStatus.DeadLetter) || message.AttachmentIds.Length == 0)
        {
            throw new ConflictException("invalid_transition", "Only an unmatched message with attachments can be assigned to a vendor.");
        }

        if (!await vendors.CanTransactAsync(vendorId, message.LegalEntityId, ct))
        {
            throw new ConflictException("vendor_cannot_transact", "The vendor is not active or is not linked to this legal entity.");
        }

        await CreateDraftAsync(message, vendorId, current.UserId, ActorKind.Staff, ct);
        audit.Record(new AuditEntry("invoice.intake.assigned", "IntakeMessage", messageId.ToString(), message.LegalEntityId,
            JsonSerializer.Serialize(new { vendorId, invoiceId = message.InvoiceId })));
        await db.SaveChangesAsync(ct);
        return Map(message);
    }

    public async Task<IntakeMessageDto> DismissAsync(Guid messageId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ValidationFailedException("reason_required", "A reason is required (max 500 characters).");
        var message = await db.IntakeMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct) ?? throw new NotFoundException("Message");
        if (message.Status is not (IntakeStatus.Unmatched or IntakeStatus.Failed or IntakeStatus.DeadLetter))
        {
            throw new ConflictException("invalid_transition", "This message is already handled.");
        }

        message.Status = IntakeStatus.Dismissed;
        message.Note = reason.Trim();
        message.ProcessedAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("invoice.intake.dismissed", "IntakeMessage", messageId.ToString(), message.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(message);
    }

    // --- mailbox configuration ---------------------------------------------------------------------------------

    public async Task<MailboxDto?> GetMailboxAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        var box = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.LegalEntityId == legalEntityId, ct);
        return box is null ? null : new MailboxDto(box.LegalEntityId, box.Address, box.Enabled, box.LastPolledAt, box.LastError);
    }

    public async Task<MailboxDto> SaveMailboxAsync(Guid legalEntityId, string address, bool enabled, CancellationToken ct = default)
    {
        address = address?.Trim().ToLowerInvariant() ?? "";
        if (!(MailAddress.TryCreate(address, out var parsed) && parsed.Address == address) || address.Length > 320)
        {
            throw new ValidationFailedException("invalid_email", "The mailbox address is not valid.");
        }

        if (!await entities.ExistsAsync(legalEntityId, ct)) throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist.");
        if (await db.Mailboxes.AnyAsync(m => m.Address == address && m.LegalEntityId != legalEntityId, ct))
        {
            throw new ConflictException("mailbox_in_use", "Another legal entity already uses this mailbox.");
        }

        var box = await db.Mailboxes.FirstOrDefaultAsync(m => m.LegalEntityId == legalEntityId, ct);
        if (box is null)
        {
            box = new InvoiceMailbox { Id = Guid.NewGuid(), LegalEntityId = legalEntityId };
            db.Mailboxes.Add(box);
        }

        box.Address = address;
        box.Enabled = enabled;
        box.UpdatedBy = current.UserId;
        box.UpdatedAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("invoice.mailbox.saved", "InvoiceMailbox", box.Id.ToString(), legalEntityId,
            JsonSerializer.Serialize(new { address, enabled })));
        await db.SaveChangesAsync(ct);
        return new MailboxDto(legalEntityId, address, enabled, box.LastPolledAt, box.LastError);
    }

    // --- worker scans ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<(Guid TenantId, Guid LegalEntityId)>> EnabledMailboxesAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        var rows = await db.Mailboxes.AsNoTracking().Where(m => m.Enabled).Select(m => new { m.TenantId, m.LegalEntityId }).ToListAsync(ct);
        return rows.Select(r => (r.TenantId, r.LegalEntityId)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> TenantsWithPendingExtractionAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await (from a in db.Attachments.AsNoTracking()
                      join i in db.Invoices.AsNoTracking() on a.InvoiceId equals i.Id
                      where a.Role == AttachmentRole.Original && i.Status == InvoiceStatus.Draft
                            && !db.Extractions.Any(e => e.AttachmentId == a.AttachmentId)
                      select a.TenantId).Distinct().ToListAsync(ct);
    }

    private static IntakeMessageDto Map(IntakeMessage m) => new(m.Id, m.LegalEntityId, m.FromAddress, m.Subject, m.ReceivedAt, m.Status, m.VendorId, m.InvoiceId,
        m.Attempts, m.Note, m.AttachmentIds.Length == 0 ? 0 : m.AttachmentIds.Split(',').Length);

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}

internal static class AttachmentServiceNames
{
    /// <summary>Must match the owner type the Documents module accepts for reassignment.</summary>
    public const string IntakeOwner = "IntakeMessage";
}
