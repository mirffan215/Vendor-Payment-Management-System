using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Cloud;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.Documents.Application;
using Vpms.Documents.Infrastructure;
using Vpms.Worker;

var builder = Host.CreateApplicationBuilder(args);

static string ConnectionString(IServiceProvider sp) =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddTenancy();
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.Section));
builder.Services.AddFieldEncryption();
builder.Services.AddKeyVaultKeyEncryption();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.AddBlobStorage();
builder.Services.Configure<DocumentsOptions>(builder.Configuration.GetSection(DocumentsOptions.Section));
builder.Services.AddDocumentsModule(ConnectionString);
builder.Services.AddAuditModule(ConnectionString);
builder.Services.AddSingleton<IAuditAnchorSink>(sp => new FileAuditAnchorSink(
    sp.GetRequiredService<IConfiguration>()["Audit:AnchorDirectory"] ?? Path.Combine(Path.GetTempPath(), "vpms-audit-anchors")));
builder.Services.AddHostedService<AuditChainJob>();
builder.Services.AddHostedService<AttachmentScanJob>();

await builder.Build().RunAsync();
