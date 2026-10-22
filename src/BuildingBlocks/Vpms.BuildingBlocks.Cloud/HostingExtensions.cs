using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Vpms.BuildingBlocks.Cloud;

/// <summary>
/// Where the ASP.NET data-protection key ring lives. It protects the session cookies, anti-forgery tokens and sign-in challenges, so
/// every instance (API and worker, before and after a deployment) must share it, and it must not sit unencrypted on disk.
/// </summary>
public sealed class DataProtectionSettings
{
    public const string Section = "DataProtection";

    /// <summary>"Default" (framework default, development and tests only), "FileSystem" (a shared, protected volume) or "AzureBlob" (production).</summary>
    public string Provider { get; set; } = "Default";

    /// <summary>Directory for the FileSystem provider.</summary>
    public string? Directory { get; set; }

    /// <summary>Full URI of the blob that holds the key ring (AzureBlob provider). Access uses the workload identity, never a key.</summary>
    public string? BlobUri { get; set; }

    /// <summary>Key Vault key (full identifier) that wraps the key ring at rest.</summary>
    public string? KeyVaultKeyId { get; set; }
}

public static class HostingExtensions
{
    public const string KeyVaultUriSetting = "KeyVault:Uri";

    /// <summary>
    /// Loads settings from Key Vault when KeyVault:Uri is set. A vault secret named "Secrets--erp-secret" becomes "Secrets:erp-secret",
    /// which is where <c>ISecretReader</c> looks; "ConnectionStrings--Default" works the same way. Values are re-read on a timer so a rotated
    /// secret is picked up without a restart.
    /// </summary>
    public static T AddVpmsKeyVault<T>(this T configuration) where T : IConfigurationBuilder, IConfiguration
    {
        var uri = configuration[KeyVaultUriSetting];
        if (string.IsNullOrWhiteSpace(uri)) return configuration;
        var minutes = int.TryParse(configuration["KeyVault:ReloadMinutes"], out var m) && m > 0 ? m : 15;
        configuration.AddAzureKeyVault(new Uri(uri), new DefaultAzureCredential(),
            new AzureKeyVaultConfigurationOptions { ReloadInterval = TimeSpan.FromMinutes(minutes) });
        return configuration;
    }

    public static IServiceCollection AddVpmsDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(DataProtectionSettings.Section);
        var settings = new DataProtectionSettings
        {
            Provider = section["Provider"] ?? "Default", Directory = section["Directory"], BlobUri = section["BlobUri"], KeyVaultKeyId = section["KeyVaultKeyId"],
        };
        var builder = services.AddDataProtection().SetApplicationName("vpms");
        switch (settings.Provider)
        {
            case "Default":
                break;
            case "FileSystem":
                builder.PersistKeysToFileSystem(new DirectoryInfo(settings.Directory
                    ?? throw new InvalidOperationException("DataProtection:Directory is required for the FileSystem provider.")));
                break;
            case "AzureBlob":
                var credential = new DefaultAzureCredential();
                builder.PersistKeysToAzureBlobStorage(new Uri(settings.BlobUri
                    ?? throw new InvalidOperationException("DataProtection:BlobUri is required for the AzureBlob provider.")), credential);
                if (!string.IsNullOrWhiteSpace(settings.KeyVaultKeyId)) builder.ProtectKeysWithAzureKeyVault(new Uri(settings.KeyVaultKeyId), credential);
                break;
            default:
                throw new InvalidOperationException($"Unknown data-protection provider '{settings.Provider}'.");
        }

        return services;
    }
}
