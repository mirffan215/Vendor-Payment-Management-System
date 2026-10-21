using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Persistence;
using Vpms.Documents.Application;
using Vpms.Documents.Contracts;

namespace Vpms.Documents.Infrastructure;

public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddOptions<DocumentsOptions>();
        services.AddTenantDbContext<DocumentsDbContext>(connectionString);
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IAttachmentScanService, AttachmentScanService>();
        services.AddSingleton<IVirusScanner>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<DocumentsOptions>>().Value;
            return o.Scanner switch
            {
                "ClamAv" => new ClamAvScanner(o.ClamAv),
                "Eicar" => new EicarVirusScanner(),
                _ => throw new InvalidOperationException($"Unknown virus scanner '{o.Scanner}'."),
            };
        });
        return services;
    }
}
