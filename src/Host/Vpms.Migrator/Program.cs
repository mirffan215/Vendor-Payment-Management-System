using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Cloud;
using Vpms.BuildingBlocks.Storage;
using Vpms.Identity.Domain;

namespace Vpms.Migrator;

internal static class MigratorEntry
{
    private static async Task<int> Main(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
                 ?? throw new InvalidOperationException("ConnectionStrings__Migrator is required.");

        if (args.Length > 0 && args[0] == "provision-tenant")
        {
            var o = ParseOptions(args.Skip(1).ToArray());
            string Get(string key) => o.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required.");
            var kind = Get("idp-kind").ToLowerInvariant() switch
            {
                "entra" => IdentityProviderKind.EntraId,
                "google" => IdentityProviderKind.Google,
                "oidc" => IdentityProviderKind.GenericOidc,
                _ => throw new ArgumentException("--idp-kind must be entra, google or oidc."),
            };
            var result = await TenantProvisioner.ProvisionAsync(cs, new ProvisionRequest(
                Get("name"), Get("slug"), Get("admin-email"), kind, Get("idp-name"), Get("idp-authority"), Get("idp-client-id"),
                Get("idp-secret-ref"), Get("domains").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                o.GetValueOrDefault("locale", "en")));
            Console.WriteLine($"Tenant {result.TenantId} created. Identity provider {result.ProviderId}. {Get("admin-email")} becomes tenant administrator on first sign-in.");
            return 0;
        }

        // A database that is still starting, failing over or throttling answers with a transient error: wait and try again before failing the deployment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Leaving the service (ADR-0024): suspend, resume, and the final shred.
        if (args.Length > 0 && args[0] is "suspend-tenant" or "resume-tenant" or "shred-tenant")
        {
            var o = ParseOptions(args.Skip(1).ToArray());
            string Get(string key) => o.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required.");
            switch (args[0])
            {
                case "suspend-tenant":
                    await TenantOffboarding.SuspendAsync(cs, Get("slug"), Get("reason"));
                    Console.WriteLine("{\"level\":\"information\",\"message\":\"Tenant suspended.\"}");
                    break;
                case "resume-tenant":
                    await TenantOffboarding.ResumeAsync(cs, Get("slug"), Get("reason"));
                    Console.WriteLine("{\"level\":\"information\",\"message\":\"Tenant resumed.\"}");
                    break;
                default:
                    var storage = new ConfigurationBuilder().AddEnvironmentVariables().Build();
                    var services = new ServiceCollection();
                    services.Configure<Vpms.BuildingBlocks.Cloud.StorageOptions>(storage.GetSection(Vpms.BuildingBlocks.Cloud.StorageOptions.Section));
                    services.AddBlobStorage();
                    await using (var provider = services.BuildServiceProvider())
                    {
                        var result = await TenantOffboarding.ShredAsync(cs, Get("slug"), Get("confirm"), int.Parse(o.GetValueOrDefault("min-days", "30"), System.Globalization.CultureInfo.InvariantCulture),
                            provider.GetRequiredService<IBlobBackend>(), TimeProvider.System);
                        Console.WriteLine($"{{\"level\":\"information\",\"message\":\"Tenant shredded: {result.FilesDeleted} files deleted, {result.KeysDestroyed} keys destroyed.\"}}");
                    }

                    break;
            }

            return 0;
        }

        await DatabaseMigrator.MigrateAsync(cs);
                break;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (attempt < 6 && Transient(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5);
                Console.WriteLine($"{{\"level\":\"warning\",\"message\":\"Migration attempt {attempt} hit a transient SQL error ({ex.Number}); retrying in {delay.TotalSeconds:0}s.\"}}");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                // The type and message only: the connection string is never part of an exception text we print.
                Console.Error.WriteLine($"{{\"level\":\"error\",\"message\":\"Migration failed: {ex.GetType().Name}\"}}");
                return 1;
            }
        }

        Console.WriteLine("{\"level\":\"information\",\"message\":\"Migrations applied.\"}");

        // Optional: provision the restricted application login (local development and first deployment).
        var appLogin = Environment.GetEnvironmentVariable("VPMS_APP_LOGIN");
        var appPassword = Environment.GetEnvironmentVariable("VPMS_APP_LOGIN_PASSWORD");
        if (!string.IsNullOrEmpty(appLogin) && !string.IsNullOrEmpty(appPassword))
        {
            await DatabaseMigrator.EnsureAppLoginAsync(cs, appLogin, appPassword);
            Console.WriteLine($"Application login '{appLogin}' is ready.");
        }

        return 0;
    }

    static bool Transient(Microsoft.Data.SqlClient.SqlException ex) =>
        ex.Number is -2 or 20 or 64 or 233 or 953 or 4060 or 10928 or 10929 or 40197 or 40501 or 40613 or 49918 or 49919 or 49920 || ex.IsTransient;

    static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i + 1 < a.Length; i += 2)
        {
            d[a[i].TrimStart('-')] = a[i + 1];
        }

        return d;
    }
}
