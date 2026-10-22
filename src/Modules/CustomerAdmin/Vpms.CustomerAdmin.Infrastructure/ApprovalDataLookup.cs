using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.CustomerAdmin.Contracts;

namespace Vpms.CustomerAdmin.Infrastructure;

public sealed class ApprovalDataLookup(CustomerAdminDbContext db) : IApprovalDataLookup
{
    public async Task<IReadOnlyList<MatrixRuleInfo>> GetMatrixRulesAsync(Guid legalEntityId, CancellationToken ct = default) =>
        (await db.ApprovalMatrixRules.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntityId && r.Status == "Active")
            .OrderBy(r => r.Priority).ThenBy(r => r.CreatedAt).ToListAsync(ct))
        .Select(r => new MatrixRuleInfo(r.Id, r.Name, r.Priority, r.ConditionsJson, r.ApproverResolutionJson, r.Levels)).ToList();

    public async Task<bool> HasAuthorityAsync(Guid legalEntityId, Guid userId, decimal amount, string currency,
        IReadOnlyDictionary<string, string> dimensions, DateTimeOffset at, CancellationToken ct = default)
    {
        var rows = await db.ApprovalAuthorities.AsNoTracking()
            .Where(a => a.LegalEntityId == legalEntityId && a.UserId == userId && a.Status == "Active"
                        && a.CurrencyCode == currency && a.MaxAmount >= amount && a.ValidFrom <= at && (a.ValidTo == null || a.ValidTo > at))
            .Select(a => a.DimensionScopeJson).ToListAsync(ct);

        return rows.Any(scope => DimensionsAllowed(scope, dimensions));
    }

    /// <summary>A scope like {"CostCenter":["CC1","CC2"]} limits the authority to invoices carrying one of those values for each listed dimension.</summary>
    internal static bool DimensionsAllowed(string? scopeJson, IReadOnlyDictionary<string, string> dimensions)
    {
        if (string.IsNullOrWhiteSpace(scopeJson)) return true;
        Dictionary<string, string[]>? scope;
        try
        {
            scope = JsonSerializer.Deserialize<Dictionary<string, string[]>>(scopeJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return false; // an unreadable scope never widens authority
        }

        if (scope is null || scope.Count == 0) return true;
        return scope.All(kv => dimensions.TryGetValue(kv.Key, out var v) && kv.Value.Contains(v, StringComparer.OrdinalIgnoreCase));
    }
}
