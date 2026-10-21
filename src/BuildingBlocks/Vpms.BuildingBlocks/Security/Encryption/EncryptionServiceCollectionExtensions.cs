using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Vpms.BuildingBlocks.Security.Encryption;

public static class EncryptionServiceCollectionExtensions
{
    /// <summary>
    /// Registers field protection. The key-encryption key is the Local provider unless one is already registered
    /// (the Key Vault implementation registers itself first when configured).
    /// </summary>
    public static IServiceCollection AddFieldEncryption(this IServiceCollection services)
    {
        services.AddOptions<EncryptionOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IKeyEncryptionKey>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<EncryptionOptions>>().Value;
            if (o.Provider != "Local")
            {
                throw new EncryptionException($"Encryption provider '{o.Provider}' is not registered.");
            }

            return new LocalKeyEncryptionKey(o.LocalKek
                ?? throw new EncryptionException("Encryption:LocalKek is required for the Local provider."));
        });
        services.AddSingleton<TenantKeyRing>();
        services.AddSingleton<ITenantKeyManager>(sp => sp.GetRequiredService<TenantKeyRing>());
        services.AddScoped<IFieldProtector, FieldProtector>();
        return services;
    }
}
