using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.Worker;

var builder = Host.CreateApplicationBuilder(args);

static string ConnectionString(IServiceProvider sp) =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddTenancy();
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.Section));
builder.Services.AddFieldEncryption();
builder.Services.AddAuditModule(ConnectionString);
builder.Services.AddSingleton<IAuditAnchorSink>(sp => new FileAuditAnchorSink(
    sp.GetRequiredService<IConfiguration>()["Audit:AnchorDirectory"] ?? Path.Combine(Path.GetTempPath(), "vpms-audit-anchors")));
builder.Services.AddHostedService<AuditChainJob>();

await builder.Build().RunAsync();
