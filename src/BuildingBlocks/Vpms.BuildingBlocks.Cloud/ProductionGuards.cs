using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Vpms.BuildingBlocks.Cloud;

/// <summary>
/// Refuses to start in Production with a setting that is only meant for development. Each check names the setting so the failure is
/// fixed in configuration, not worked around in code.
/// </summary>
public static class ProductionGuards
{
    public static IReadOnlyList<string> Problems(IConfiguration c, bool web = true)
    {
        var problems = new List<string>();
        bool Flag(string key) => bool.TryParse(c[key], out var v) && v;
        bool Is(string key, string value, string fallback = "") => string.Equals(c[key] ?? fallback, value, StringComparison.OrdinalIgnoreCase);

        if (Is("Encryption:Provider", "Local", "Local")) problems.Add("Encryption:Provider must be KeyVault; the Local key is for development only.");
        if (Is("Storage:Provider", "InMemory", "InMemory")) problems.Add("Storage:Provider must be Azure or S3.");
        if (Is("Documents:Scanner", "Eicar")) problems.Add("Documents:Scanner must be ClamAv; Eicar only detects a test string.");
        if (string.IsNullOrWhiteSpace(c[HostingExtensions.KeyVaultUriSetting])) problems.Add("KeyVault:Uri is required; secrets are read from Key Vault.");
        if (c.GetConnectionString("Default") is { } cs)
        {
            if (cs.Contains("TrustServerCertificate=True", StringComparison.OrdinalIgnoreCase)) problems.Add("ConnectionStrings:Default must not trust the server certificate blindly.");
            if (cs.Contains("Encrypt=False", StringComparison.OrdinalIgnoreCase)) problems.Add("ConnectionStrings:Default must encrypt the connection.");
        }

        if (!web) return problems;
        if (!Is("DataProtection:Provider", "AzureBlob") && !Is("DataProtection:Provider", "FileSystem"))
        {
            problems.Add("DataProtection:Provider must be AzureBlob (or FileSystem on a shared, protected volume) so every instance shares one key ring.");
        }

        if (Is("DataProtection:Provider", "AzureBlob") && string.IsNullOrWhiteSpace(c["DataProtection:KeyVaultKeyId"]))
        {
            problems.Add("DataProtection:KeyVaultKeyId is required so the key ring is encrypted at rest.");
        }

        var hosts = c["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(hosts) || hosts.Trim() == "*") problems.Add("AllowedHosts must list the public host names, not '*'.");
        if (Flag("Auth:AllowInsecureMetadata")) problems.Add("Auth:AllowInsecureMetadata must be false.");
        if (Flag("Notifications:AllowInternalHosts")) problems.Add("Notifications:AllowInternalHosts must be false.");
        if (string.IsNullOrWhiteSpace(c["App:BaseUrl"])) problems.Add("App:BaseUrl is required (links in notifications).");

        var forwarded = c.GetSection("ForwardedHeaders");
        if (!forwarded.GetSection("KnownProxies").GetChildren().Any() && !forwarded.GetSection("KnownNetworks").GetChildren().Any() && !Flag("ForwardedHeaders:TrustImmediatePeer"))
        {
            problems.Add("ForwardedHeaders:KnownProxies, KnownNetworks or TrustImmediatePeer must be set so client addresses and https are read correctly behind the ingress.");
        }

        return problems;
    }

    public static void EnsureSafe(IConfiguration configuration, IHostEnvironment environment, bool web = true)
    {
        if (!environment.IsProduction()) return;
        var problems = Problems(configuration, web);
        if (problems.Count > 0) throw new InvalidOperationException("Unsafe Production configuration:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => " - " + p)));
    }
}
