using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Application;
using Vpms.Audit.Domain;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Infrastructure;

public sealed class AuditEvidenceService(
    IAuditQueryService query, IAuditChainVerifier chain, AuditDbContext db, IFieldProtector protector, ITenantContext tenant, IAuditPort audit, AuditContext actor, TimeProvider clock)
    : IAuditEvidenceService
{
    public const string SignatureColumn = "audit.EvidenceBundle.Manifest";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<EvidenceBundle> BuildAsync(AuditQuery filter, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var events = new List<AuditEvent>();
        await foreach (var e in query.StreamAsync(filter, entityScope, ct))
        {
            if (events.Count >= IAuditEvidenceService.MaxEvents) throw new ValidationFailedException("too_many_events", $"More than {IAuditEvidenceService.MaxEvents} events match. Narrow the filter.");
            events.Add(e);
        }

        var verification = await chain.VerifyAsync(tenantId, ct);
        var head = await db.ChainHeads.AsNoTracking().FirstOrDefaultAsync(h => h.TenantId == tenantId, ct);
        var anchors = await db.Anchors.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Sequence).ToListAsync(ct);
        var now = clock.GetUtcNow();

        var files = new List<(string Name, byte[] Bytes)>
        {
            ("events.json", Utf8(JsonSerializer.Serialize(events.Select(Row).ToList(), Json))),
            ("events.csv", Csv(events)),
            ("chain-proof.json", Utf8(JsonSerializer.Serialize(new
            {
                tenantId,
                checkedAt = now,
                chainValid = verification.IsValid,
                verification.EventsChecked,
                verification.FirstBrokenSequence,
                verification.Problem,
                head = head is null ? null : new { head.Sequence, head.Hash },
                anchors = anchors.Select(a => new { a.Sequence, a.Hash, a.ExternalRef, a.AnchoredAt }),
                note = "The chain proof covers the whole tenant chain. The events file holds only the events that matched the filter; each carries its own previous hash and hash, so its content can be checked on its own, and consecutive sequence numbers can be checked for linkage.",
            }, Json))),
        };

        var manifest = new
        {
            version = 1,
            tenantId,
            exportedAt = now,
            exportedBy = actor.ActorId,
            filter = new { filter.LegalEntityId, filter.ActorId, filter.Action, filter.EntityType, filter.EntityId, filter.CorrelationId, filter.From, filter.To, filter.AfterSequence },
            eventCount = events.Count,
            chainValid = verification.IsValid,
            hashAlgorithm = "SHA-256",
            files = files.Select(f => new { name = f.Name, sha256 = Hex(f.Bytes), bytes = f.Bytes.Length }).ToList(),
        };
        var manifestBytes = Utf8(JsonSerializer.Serialize(manifest, Json));
        var manifestHash = Hex(manifestBytes);
        var signature = await protector.BlindIndexAsync(SignatureColumn, manifestHash, ct) ?? throw new InvalidOperationException("Signing key unavailable.");

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, bytes) in files.Append(("manifest.json", manifestBytes)).Append(("manifest.sig", Utf8(signature)))) Add(zip, name, bytes);
        }

        await audit.WriteNowAsync(new AuditEntry("audit.evidence.exported", "AuditEvidence", manifestHash, filter.LegalEntityId, JsonSerializer.Serialize(new { events = events.Count, manifestSha256 = manifestHash, chainValid = verification.IsValid })), ct);
        return new EvidenceBundle(stream.ToArray(), $"audit-evidence-{now:yyyyMMdd-HHmm}.zip", events.Count, manifestHash, verification.IsValid);
    }

    public async Task<EvidenceVerification> VerifyAsync(Stream bundle, CancellationToken ct = default)
    {
        var problems = new List<string>();
        Dictionary<string, byte[]> entries;
        try
        {
            using var zip = new ZipArchive(bundle, ZipArchiveMode.Read);
            if (zip.Entries.Count > 20 || zip.Entries.Sum(e => e.Length) > 200_000_000) return Failed("The bundle is not a valid evidence bundle.");
            entries = zip.Entries.ToDictionary(e => e.FullName, e => ReadAll(e));
        }
        catch (InvalidDataException)
        {
            return Failed("The file is not a zip archive.");
        }

        foreach (var required in new[] { "manifest.json", "manifest.sig", "events.json", "events.csv", "chain-proof.json" })
        {
            if (!entries.ContainsKey(required)) problems.Add($"Missing {required}.");
        }

        if (problems.Count > 0) return new EvidenceVerification(false, 0, false, false, false, false, problems);

        var manifestHash = Hex(entries["manifest.json"]);
        var expected = await protector.BlindIndexAsync(SignatureColumn, manifestHash, ct);
        var signatureValid = expected is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries["manifest.sig"]).Trim()));
        if (!signatureValid) problems.Add("The manifest signature is not valid for this organisation.");

        var filesIntact = true;
        using (var doc = JsonDocument.Parse(entries["manifest.json"]))
        {
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = f.GetProperty("name").GetString()!;
                if (!entries.TryGetValue(name, out var bytes) || Hex(bytes) != f.GetProperty("sha256").GetString())
                {
                    filesIntact = false;
                    problems.Add($"{name} does not match the manifest.");
                }
            }
        }

        var eventsIntact = true;
        var linked = true;
        var count = 0;
        AuditEvent? previous = null;
        using (var events = JsonDocument.Parse(entries["events.json"]))
        {
            foreach (var row in events.RootElement.EnumerateArray())
            {
                var e = FromRow(row);
                count++;
                if (AuditHasher.Compute(e.PrevHash, e) != e.Hash)
                {
                    eventsIntact = false;
                    problems.Add($"Event {e.Sequence} does not match its hash.");
                }

                if (previous is not null && e.Sequence == previous.Sequence + 1 && e.PrevHash != previous.Hash)
                {
                    linked = false;
                    problems.Add($"Event {e.Sequence} is not linked to event {previous.Sequence}.");
                }

                previous = e;
            }
        }

        return new EvidenceVerification(problems.Count == 0, count, signatureValid, filesIntact, eventsIntact, linked, problems);

        static EvidenceVerification Failed(string problem) => new(false, 0, false, false, false, false, [problem]);
    }

    // --- helpers ---------------------------------------------------------------------------------------------

    private static object Row(AuditEvent e) => new
    {
        e.Sequence, e.Id, e.TenantId, e.LegalEntityId, occurredAt = Canonical(e.OccurredAt), e.ActorType, e.ActorId, e.OnBehalfOfId, e.Action, e.EntityType, e.EntityId, e.CorrelationId,
        e.IpAddress, e.BeforeHash, e.AfterHash, changeSummaryJson = e.ChangeSummaryJson, e.PrevHash, e.Hash,
    };

    private static string Canonical(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static AuditEvent FromRow(JsonElement r)
    {
        string? S(string n) => r.GetProperty(n).ValueKind == JsonValueKind.Null ? null : r.GetProperty(n).GetString();
        Guid? G(string n) => S(n) is { } s ? Guid.Parse(s) : null;
        return new AuditEvent
        {
            Sequence = r.GetProperty("sequence").GetInt64(), Id = Guid.Parse(S("id")!), TenantId = Guid.Parse(S("tenantId")!), LegalEntityId = G("legalEntityId"),
            OccurredAt = DateTimeOffset.Parse(S("occurredAt")!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            ActorType = S("actorType") ?? "", ActorId = G("actorId"), OnBehalfOfId = G("onBehalfOfId"), Action = S("action") ?? "", EntityType = S("entityType") ?? "", EntityId = S("entityId"),
            CorrelationId = S("correlationId"), IpAddress = S("ipAddress"), BeforeHash = S("beforeHash"), AfterHash = S("afterHash"), ChangeSummaryJson = S("changeSummaryJson"),
            PrevHash = S("prevHash") ?? "", Hash = S("hash") ?? "",
        };
    }

    private static byte[] Csv(IEnumerable<AuditEvent> events)
    {
        var sb = new StringBuilder("Sequence,OccurredAt,Action,EntityType,EntityId,ActorType,ActorId,LegalEntityId,CorrelationId,IpAddress,ChangeSummary,PrevHash,Hash\r\n");
        foreach (var e in events)
        {
            sb.Append(string.Join(',', e.Sequence.ToString(CultureInfo.InvariantCulture), Cell(Canonical(e.OccurredAt)), Cell(e.Action), Cell(e.EntityType), Cell(e.EntityId), Cell(e.ActorType), Cell(e.ActorId?.ToString()),
                Cell(e.LegalEntityId?.ToString()), Cell(e.CorrelationId), Cell(e.IpAddress), Cell(e.ChangeSummaryJson), Cell(e.PrevHash), Cell(e.Hash))).Append("\r\n");
        }

        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(bytes);
    }

    private static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
