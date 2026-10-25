using Vpms.ErpIntegration.Infrastructure;
using Vpms.Notifications.Infrastructure;
using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Cloud;
using Vpms.BuildingBlocks.Observability;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Documents.Application;
using Vpms.Identity.Infrastructure;
using Vpms.Invoices.Infrastructure;
using Vpms.Payments.Infrastructure;
using Vpms.Workflow.Infrastructure;
using Vpms.Documents.Infrastructure;
using Vpms.Vendors.Infrastructure;
using Vpms.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Container liveness probe: "Vpms.Worker --healthcheck" exits 0 while the heartbeat file is fresh.
if (args.Contains("--healthcheck")) return WorkerHeartbeat.IsAlive(builder.Configuration, TimeSpan.FromSeconds(60)) ? 0 : 1;

builder.Configuration.AddVpmsKeyVault();
builder.Services.AddVpmsObservability(builder.Configuration, "vpms-worker");

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
builder.Services.AddCustomerAdminModule(ConnectionString);
builder.Services.AddIdentityModule(ConnectionString);
builder.Services.AddVendorsModule(ConnectionString);
builder.Services.AddInvoicesModule(ConnectionString);
builder.Services.AddWorkflowModule(ConnectionString);
builder.Services.AddPaymentsModule(ConnectionString);
builder.Services.AddErpIntegrationModule(ConnectionString);
builder.Services.AddD365FinanceAdapter(builder.Configuration);
builder.Services.AddNotificationsModule(ConnectionString, o =>
{
    o.BaseUrl = builder.Configuration["App:BaseUrl"] ?? "";
    o.AllowInternalHosts = builder.Configuration.GetValue("Notifications:AllowInternalHosts", false);
});
builder.Services.Configure<Vpms.Notifications.Infrastructure.GraphMailOptions>(o =>
{
    o.BaseUrl = builder.Configuration["Notifications:GraphBaseUrl"] ?? o.BaseUrl;
    o.AuthorityUrl = builder.Configuration["Notifications:GraphAuthorityUrl"] ?? o.AuthorityUrl;
});
// Country tax rule plug-ins for received invoices. Removing a line switches that country back to the standard checks.
builder.Services.AddSingleton<Vpms.Invoices.Domain.Tax.ITaxRuleProvider, Vpms.Tax.Sa.SaudiVatProvider>();
builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection(GraphOptions.Section));
builder.Services.AddAuditModule(ConnectionString);
builder.Services.AddAnchorBackend(AuditModule.AnchorBackendKey);
builder.Services.AddAuditAnchorSink();
builder.Services.AddHostedService<AuditChainJob>();
builder.Services.AddHostedService<AttachmentScanJob>();
builder.Services.AddHostedService<BankChangeExpiryJob>();
builder.Services.AddHostedService<EmailIntakeJob>();
builder.Services.AddHostedService<InvoiceExtractionJob>();
builder.Services.AddHostedService<InvoiceVerificationJob>();
builder.Services.AddHostedService<ApprovalOutcomeJob>();
builder.Services.AddHostedService<WorkflowTimerJob>();
builder.Services.AddHostedService<PaymentIntegrityJob>();
builder.Services.AddHostedService<ErpOutboundJob>();
builder.Services.AddHostedService<NotificationJob>();
builder.Services.AddHostedService<RetentionJob>();

builder.Services.AddHostedService<WorkerHeartbeat>();
var host = builder.Build();
ProductionGuards.EnsureSafe(host.Services.GetRequiredService<IConfiguration>(), host.Services.GetRequiredService<IHostEnvironment>(), web: false);
await host.RunAsync();
return 0;
