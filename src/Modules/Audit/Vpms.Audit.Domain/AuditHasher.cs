using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vpms.Audit.Domain;

/// <summary>
/// Hash = SHA-256( PrevHash + "\n" + canonicalJson ), hex lower-case. The canonical form is a JSON array of the
/// event's fields in a fixed order, so the writer and the verifier always agree.
/// </summary>
public static class AuditHasher
{
    public static readonly string GenesisHash = new('0', 64);

    public static string Canonical(AuditEvent e) => JsonSerializer.Serialize(new[]
    {
        e.Sequence.ToString(CultureInfo.InvariantCulture),
        e.TenantId.ToString("D"),
        e.LegalEntityId?.ToString("D"),
        e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
        e.ActorType,
        e.ActorId?.ToString("D"),
        e.OnBehalfOfId?.ToString("D"),
        e.Action,
        e.EntityType,
        e.EntityId,
        e.CorrelationId,
        e.IpAddress,
        e.BeforeHash,
        e.AfterHash,
        e.ChangeSummaryJson,
    });

    public static string Compute(string prevHash, AuditEvent e) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prevHash + "\n" + Canonical(e))));
}
