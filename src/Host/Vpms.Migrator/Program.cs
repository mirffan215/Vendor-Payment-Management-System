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

        await DatabaseMigrator.MigrateAsync(cs);
        Console.WriteLine("Migrations applied.");

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
