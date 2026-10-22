using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Payments.Application;
using Vpms.Payments.Domain;
using Vpms.Vendors.Contracts;

namespace Vpms.Payments.Infrastructure;

public sealed class TreasuryService(
    PaymentsDbContext db, PaymentProposalService proposals, IPaymentProposalService proposalReads, PaymentRechecker rechecker, IBankDetailsLookup bank, ILegalEntityLookup entities,
    IInstructionExporter exporter, IFieldProtector protector, IAuditPort audit, ICurrentUser current, TimeProvider clock) : ITreasuryService
{
    private const string CsvColumn = "payment.PaymentInstruction.Csv";
    private const string JsonColumn = "payment.PaymentInstruction.Json";
    private const string SignatureColumn = "payment.PaymentInstruction.Signature";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReleaseResult> ReleaseAsync(Guid proposalId, CancellationToken ct = default)
    {
        var releaser = current.UserId;
        var p = await db.Proposals.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == proposalId, ct) ?? throw new NotFoundException("Payment proposal");
        PaymentProposalService.RequireStatus(p, ProposalStatus.Approved, "Only an approved proposal can be released.");
        var requests = await proposals.RequestsOf(p, ct);

        // 1. separation of duties: refused before anything else is looked at
        var separation = ReleaseSeparation.Evaluate(releaser, p, requests);
        if (separation.Count > 0)
        {
            await RecordDenialAsync(p, releaser, "separation", [], separation, ct);
            await audit.WriteNowAsync(new AuditEntry("sod.payment.blocked", "PaymentProposal", proposalId.ToString(), p.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "release", rule = "release-separation", findings = separation.Select(f => f.Code) })), ct);
            throw new SegregationOfDutiesException("release-separation",
                "Releasing a payment must be done by someone who did not prepare, approve or take part in these invoices, their approval or the vendor's bank details.");
        }

        // 2. the full re-check, against what each request froze
        var recheck = await rechecker.CheckAsync(requests, ct);
        if (!recheck.Passed)
        {
            if (recheck.Invalidates)
            {
                await proposals.InvalidateFromAsync(p, recheck, ct);
                AddAuthorization(p, releaser, ReleaseDecision.Denied, "recheck_failed", recheck.Findings, [], null);
                await db.SaveChangesAsync(ct);
                throw new ConflictException("recheck_failed", "Something changed since this proposal was approved, so it was invalidated. Prepare it again.");
            }

            AddAuthorization(p, releaser, ReleaseDecision.Denied, "recheck_blocked", recheck.Findings, [], null);
            await db.SaveChangesAsync(ct);
            throw new ConflictException("recheck_blocked", "A bank-detail change for one of the vendors is waiting for a decision. Try again once it is decided.");
        }

        // 3. prepare and sign the instruction
        var now = clock.GetUtcNow();
        var instructionId = Guid.NewGuid();
        var details = new Dictionary<Guid, PaymentBankDetails>();
        var payments = new List<InstructionPayment>();
        var n = 0;
        foreach (var r in requests.OrderBy(x => x.InvoiceNumber, StringComparer.Ordinal).ThenBy(x => x.Id))
        {
            if (!details.TryGetValue(r.BankDetailsId, out var d)) details[r.BankDetailsId] = d = await bank.RevealForPaymentAsync(r.BankDetailsId, ct);
            payments.Add(new InstructionPayment($"{p.ProposalNo}-{++n:D3}", d.AccountName, d.BankName, d.SwiftBic, d.CountryCode, d.Iban, d.AccountNumber, r.CurrencyCode, r.Amount, r.InvoiceNumber, r.DueDate));
        }

        var names = await entities.GetNamesAsync([p.LegalEntityId], ct);
        var model = new InstructionModel(instructionId, p.ProposalNo, p.LegalEntityId, names.GetValueOrDefault(p.LegalEntityId, ""), p.CurrencyCode, p.TotalAmount, now, releaser, payments);
        var (csv, json) = exporter.Export(model);
        var hash = HashOf(csv, json);

        var instruction = new PaymentInstruction
        {
            Id = instructionId, LegalEntityId = p.LegalEntityId, ProposalId = p.Id, Format = exporter.Format,
            CsvCipher = (await protector.ProtectAsync(CsvColumn, csv, ct))!, JsonCipher = (await protector.ProtectAsync(JsonColumn, json, ct))!,
            Hash = hash, Signature = (await protector.BlindIndexAsync(SignatureColumn, hash, ct))!, ItemCount = payments.Count, TotalAmount = p.TotalAmount, CurrencyCode = p.CurrencyCode,
            PreparedBy = releaser, PreparedAt = now,
        };
        var authorization = AddAuthorization(p, releaser, ReleaseDecision.Released, null, [], [], instructionId);
        instruction.AuthorizationId = authorization.Id;
        db.Instructions.Add(instruction);

        foreach (var r in requests) r.MarkReleased(now);
        p.MarkReleased(releaser, now);
        audit.Record(new AuditEntry("payment.proposal.released", "PaymentProposal", proposalId.ToString(), p.LegalEntityId,
            JsonSerializer.Serialize(new { p.ProposalNo, p.TotalAmount, p.CurrencyCode, items = payments.Count, instructionId })));
        audit.Record(new AuditEntry("payment.instruction.prepared", "PaymentInstruction", instructionId.ToString(), p.LegalEntityId,
            JsonSerializer.Serialize(new { instruction.Format, instruction.Hash, instruction.ItemCount })));
        await db.SaveChangesAsync(ct);

        return new ReleaseResult(await proposalReads.GetAsync(proposalId, ct), Map(instruction, p.ProposalNo));
    }

    public async Task<InstructionDto?> GetInstructionAsync(Guid proposalId, CancellationToken ct = default)
    {
        var i = await db.Instructions.AsNoTracking().FirstOrDefaultAsync(x => x.ProposalId == proposalId, ct);
        if (i is null) return null;
        return Map(i, await db.Proposals.AsNoTracking().Where(p => p.Id == proposalId).Select(p => p.ProposalNo).FirstAsync(ct));
    }

    public async Task<IReadOnlyList<AuthorizationDto>> AuthorizationsAsync(Guid proposalId, CancellationToken ct = default) =>
        (await db.Authorizations.AsNoTracking().Where(a => a.ProposalId == proposalId).OrderBy(a => a.AuthorizedAt).ToListAsync(ct)).Select(a => new AuthorizationDto(
            a.Id, a.ProposalId, a.AuthorizedBy, a.AuthorizedAt, a.Decision, a.DenialReason, JsonSerializer.Deserialize<List<RecheckFinding>>(a.RecheckJson, Json)!,
            JsonSerializer.Deserialize<List<SeparationFinding>>(a.SodJson, Json)!, a.InstructionId)).ToList();

    public async Task<InstructionFile> DownloadAsync(Guid instructionId, string kind, CancellationToken ct = default)
    {
        var i = await db.Instructions.FirstOrDefaultAsync(x => x.Id == instructionId, ct) ?? throw new NotFoundException("Payment instruction");
        var csv = await protector.UnprotectAsync(CsvColumn, i.CsvCipher, ct) ?? "";
        var json = await protector.UnprotectAsync(JsonColumn, i.JsonCipher, ct) ?? "";
        var verification = await VerifyCoreAsync(i, csv, json, ct);
        if (!verification.Valid)
        {
            await audit.WriteNowAsync(new AuditEntry("payment.instruction.tampered", "PaymentInstruction", instructionId.ToString(), i.LegalEntityId,
                JsonSerializer.Serialize(new { verification.HashMatches, verification.SignatureMatches })), ct);
            throw new ConflictException("instruction_tampered", "The stored instruction no longer matches its signature and cannot be handed off.");
        }

        var wantCsv = string.Equals(kind, "csv", StringComparison.OrdinalIgnoreCase);
        if (!wantCsv && !string.Equals(kind, "json", StringComparison.OrdinalIgnoreCase)) throw new ValidationFailedException("format_invalid", "Choose csv or json.");

        var proposalNo = await db.Proposals.AsNoTracking().Where(p => p.Id == i.ProposalId).Select(p => p.ProposalNo).FirstAsync(ct);
        var first = i.Status == InstructionStatus.Prepared;
        i.HandOff(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("payment.instruction.downloaded", "PaymentInstruction", instructionId.ToString(), i.LegalEntityId,
            JsonSerializer.Serialize(new { kind = wantCsv ? "csv" : "json", firstHandOff = first, i.Hash })));
        await db.SaveChangesAsync(ct); // the download is on record before the content leaves

        return wantCsv
            ? new InstructionFile($"{proposalNo}.csv", "text/csv; charset=utf-8", Encoding.UTF8.GetBytes(csv))
            : new InstructionFile($"{proposalNo}.json", "application/json", Encoding.UTF8.GetBytes(json));
    }

    public async Task<InstructionVerification> VerifyAsync(Guid instructionId, CancellationToken ct = default)
    {
        var i = await db.Instructions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == instructionId, ct) ?? throw new NotFoundException("Payment instruction");
        return await VerifyCoreAsync(i, await protector.UnprotectAsync(CsvColumn, i.CsvCipher, ct) ?? "", await protector.UnprotectAsync(JsonColumn, i.JsonCipher, ct) ?? "", ct);
    }

    public async Task<Guid> EntityOfInstructionAsync(Guid instructionId, CancellationToken ct = default) =>
        await db.Instructions.AsNoTracking().Where(i => i.Id == instructionId).Select(i => (Guid?)i.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Payment instruction");

    // --- internals -----------------------------------------------------------------------------------------

    private async Task<InstructionVerification> VerifyCoreAsync(PaymentInstruction i, string csv, string json, CancellationToken ct)
    {
        var hash = HashOf(csv, json);
        var signature = await protector.BlindIndexAsync(SignatureColumn, hash, ct);
        return new InstructionVerification(CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(i.Hash)),
            signature is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(i.Signature)));
    }

    public static string HashOf(string csv, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(csv).Concat(new byte[] { 0 }).Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private ReleaseAuthorization AddAuthorization(PaymentProposal p, Guid releaser, string decision, string? reason, IReadOnlyList<RecheckFinding> recheck,
        IReadOnlyList<SeparationFinding> separation, Guid? instructionId)
    {
        var a = new ReleaseAuthorization
        {
            Id = Guid.NewGuid(), LegalEntityId = p.LegalEntityId, ProposalId = p.Id, AuthorizedBy = releaser, AuthorizedAt = clock.GetUtcNow(), Decision = decision, DenialReason = reason,
            RecheckJson = JsonSerializer.Serialize(recheck, Json), SodJson = JsonSerializer.Serialize(separation, Json), InstructionId = instructionId,
        };
        db.Authorizations.Add(a);
        return a;
    }

    private async Task RecordDenialAsync(PaymentProposal p, Guid releaser, string reason, IReadOnlyList<RecheckFinding> recheck, IReadOnlyList<SeparationFinding> separation, CancellationToken ct)
    {
        AddAuthorization(p, releaser, ReleaseDecision.Denied, reason, recheck, separation, null);
        await db.SaveChangesAsync(ct);
    }

    private static InstructionDto Map(PaymentInstruction i, string proposalNo) =>
        new(i.Id, i.ProposalId, proposalNo, i.Format, i.Hash, i.Signature, i.ItemCount, i.TotalAmount, i.CurrencyCode, i.Status, i.PreparedBy, i.PreparedAt, i.HandedOffBy, i.HandedOffAt);
}
