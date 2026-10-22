using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Cloud;
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
// Country tax rule plug-ins for received invoices. Removing a line switches that country back to the standard checks.
builder.Services.AddSingleton<Vpms.Invoices.Domain.Tax.ITaxRuleProvider, Vpms.Tax.Sa.SaudiVatProvider>();
builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection(GraphOptions.Section));
builder.Services.AddAuditModule(ConnectionString);
builder.Services.AddSingleton<IAuditAnchorSink>(sp => new FileAuditAnchorSink(
    sp.GetRequiredService<IConfiguration>()["Audit:AnchorDirectory"] ?? Path.Combine(Path.GetTempPath(), "vpms-audit-anchors")));
builder.Services.AddHostedService<AuditChainJob>();
builder.Services.AddHostedService<AttachmentScanJob>();
builder.Services.AddHostedService<BankChangeExpiryJob>();
builder.Services.AddHostedService<EmailIntakeJob>();
builder.Services.AddHostedService<InvoiceExtractionJob>();
builder.Services.AddHostedService<InvoiceVerificationJob>();
builder.Services.AddHostedService<ApprovalOutcomeJob>();
builder.Services.AddHostedService<WorkflowTimerJob>();
builder.Services.AddHostedService<PaymentIntegrityJob>();

await builder.Build().RunAsync();
