using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Vendors.Application;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

public sealed class VendorRequestService(VendorsDbContext db, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IVendorRequestService
{
    public async Task<RequestDto> CreateAsync(Guid vendorId, string subject, string body, CancellationToken ct = default)
    {
        Validate(subject, body);
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct)) throw new NotFoundException("Vendor");

        var now = clock.GetUtcNow();
        var request = new VendorRequest { Id = Guid.NewGuid(), VendorId = vendorId, Subject = subject.Trim(), CreatedBy = current.UserId, CreatedAt = now };
        db.Requests.Add(request);
        db.Messages.Add(NewMessage(request, MessageDirection.ToVendor, body, now));
        audit.Record(new AuditEntry("vendor.request.created", "VendorRequest", request.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId, request.Subject })));
        await db.SaveChangesAsync(ct);
        return await Load(request.Id, ct);
    }

    public async Task<IReadOnlyList<RequestDto>> ListAsync(Guid vendorId, CancellationToken ct = default)
    {
        var ids = await db.Requests.AsNoTracking().Where(r => r.VendorId == vendorId).OrderByDescending(r => r.CreatedAt).Select(r => r.Id).ToListAsync(ct);
        var result = new List<RequestDto>();
        foreach (var id in ids) result.Add(await Load(id, ct));
        return result;
    }

    public async Task<RequestDto> ReplyAsync(Guid requestId, string body, CancellationToken ct = default)
    {
        Validate("x", body);
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw new NotFoundException("Request");
        if (request.Status == RequestStatus.Closed) throw new ConflictException("request_closed", "This request is closed.");

        request.Status = RequestStatus.Open;
        db.Messages.Add(NewMessage(request, MessageDirection.ToVendor, body, clock.GetUtcNow()));
        audit.Record(new AuditEntry("vendor.request.replied", "VendorRequest", requestId.ToString()));
        await db.SaveChangesAsync(ct);
        return await Load(requestId, ct);
    }

    public async Task<RequestDto> CloseAsync(Guid requestId, CancellationToken ct = default)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw new NotFoundException("Request");
        if (request.Status != RequestStatus.Closed)
        {
            request.Status = RequestStatus.Closed;
            request.ClosedAt = clock.GetUtcNow();
            audit.Record(new AuditEntry("vendor.request.closed", "VendorRequest", requestId.ToString()));
            await db.SaveChangesAsync(ct);
        }

        return await Load(requestId, ct);
    }

    private VendorMessage NewMessage(VendorRequest r, string direction, string body, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), VendorId = r.VendorId, RequestId = r.Id, Direction = direction, Body = body.Trim(),
        AuthorId = current.UserId, AuthorKind = ActorKind.Staff, CreatedAt = now,
    };

    private async Task<RequestDto> Load(Guid id, CancellationToken ct)
    {
        var r = await db.Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Request");
        var messages = await db.Messages.AsNoTracking().Where(m => m.RequestId == id).OrderBy(m => m.CreatedAt)
            .Select(m => new MessageDto(m.Id, m.Direction, m.Body, m.AuthorKind, m.CreatedAt)).ToListAsync(ct);
        return new RequestDto(r.Id, r.VendorId, r.Subject, r.Status, r.CreatedAt, messages);
    }

    private static void Validate(string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200) throw new ValidationFailedException("invalid_subject", "A subject is required (max 200 characters).");
        if (string.IsNullOrWhiteSpace(body) || body.Length > 4000) throw new ValidationFailedException("invalid_message", "A message is required (max 4000 characters).");
    }
}
