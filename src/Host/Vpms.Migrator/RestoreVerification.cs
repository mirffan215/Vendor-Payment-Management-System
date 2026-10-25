using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Cloud;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Storage;
using Vpms.CustomerAdmin.Infrastructure;

namespace Vpms.Migrator;

public sealed record RestoreFinding(Guid TenantId, string Check, string Detail);

/// <summary>
/// What an operator runs after restoring the database (and the file store) to see whether the restore is whole: every tenant's audit chain is intact and
/// agrees with the anchors published outside the database, every stored file the database points to exists, and the tenant keys can be unwrapped with the
/// key-encryption key. Read-only. See the restore runbook.
/// </summary>
public static class RestoreVerification
{
    public static async Task<IReadOnlyList<RestoreFinding>> RunAsync(string cs, IConfiguration configuration, CancellationToken ct = default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddTenancy();
        services.Configure<EncryptionOptions>(configuration.GetSection(EncryptionOptions.Section));
        services.AddFieldEncryption();
        services.AddKeyVaultKeyEncryption();
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.AddBlobStorage();
        services.AddAnchorBackend(AuditModule.AnchorBackendKey);
        services.AddAuditModule(_ => cs);
        services.AddAuditAnchorSink();
        services.AddCustomerAdminModule(_ => cs);
        await using var provider = services.BuildServiceProvider();
        return await RunAsync(provider, cs, ct);
    }

    public static async Task<IReadOnlyList<RestoreFinding>> RunAsync(IServiceProvider provider, string cs, CancellationToken ct = default)
    {
        var findings = new List<RestoreFinding>();

        IReadOnlyList<Guid> tenants;
        await using (var scope = provider.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().ListTenantsWithEventsAsync(ct);
        }

        foreach (var tenantId in tenants)
        {
            await using var scope = provider.CreateAsyncScope();
            var chain = await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(tenantId, ct);
            if (!chain.IsValid) findings.Add(new RestoreFinding(tenantId, "audit-chain", $"Sequence {chain.FirstBrokenSequence}: {chain.Problem}"));
        }

        // Files: every storage key in the database must exist in the file store.
        var backend = provider.GetRequiredService<IBlobBackend>();
        var byTenant = new Dictionary<Guid, List<string>>();
        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync(ct);
            await using (var platform = conn.CreateCommand())
            {
                platform.CommandText = "EXEC sp_set_session_context N'IsPlatform', 1;";
                await platform.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TenantId, StorageKey FROM documents.Attachment";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                if (!byTenant.TryGetValue(id, out var list)) byTenant[id] = list = [];
                list.Add(reader.GetString(1));
            }
        }

        foreach (var (tenantId, keys) in byTenant)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var key in backend.ListKeysAsync(TenantStoragePath.PrefixFor(tenantId), ct)) present.Add(key);
            var missing = keys.Count(k => !present.Contains(k));
            if (missing > 0) findings.Add(new RestoreFinding(tenantId, "files", $"{missing} of {keys.Count} stored files the database refers to are missing from the file store."));
        }

        // Keys: the active data key of each tenant must unwrap with the key-encryption key now configured.
        var ring = provider.GetRequiredService<TenantKeyRing>();
        foreach (var tenantId in tenants)
        {
            try
            {
                await ring.GetActiveAsync(tenantId, KeyPurpose.Data, ct);
            }
            catch (TenantKeyDestroyedException)
            {
                // A tenant that was shredded before the backup stays shredded; that is correct, not a finding.
            }
            catch (Exception ex)
            {
                findings.Add(new RestoreFinding(tenantId, "keys", $"The tenant's data key could not be unwrapped: {ex.GetType().Name}."));
            }
        }

        return findings;
    }
}
