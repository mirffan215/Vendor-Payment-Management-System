using Vpms.ErpIntegration.Infrastructure;
using Vpms.Notifications.Infrastructure;
using Vpms.Reporting.Infrastructure;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;
using Vpms.Api.Auth;
using Vpms.Api.Infrastructure;
using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Cloud;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Documents.Application;
using Vpms.Documents.Infrastructure;
using Vpms.Identity.Application;
using Vpms.Identity.Infrastructure;
using Vpms.VendorPortal.Application;
using Vpms.VendorPortal.Infrastructure;
using Vpms.Invoices.Infrastructure;
using Vpms.Payments.Infrastructure;
using Vpms.Workflow.Infrastructure;
using Vpms.Vendors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.AddVpmsLogging();

static string ConnectionString(IServiceProvider sp) =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddTenancy();
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.Section));
builder.Services.AddFieldEncryption();
builder.Services.AddKeyVaultKeyEncryption();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.AddBlobStorage();
builder.Services.Configure<DocumentsOptions>(builder.Configuration.GetSection(DocumentsOptions.Section));
builder.Services.AddCustomerAdminModule(ConnectionString);
builder.Services.AddSingleton<IProviderChangeNotifier>(sp => sp.GetRequiredService<DynamicOidcSchemes>());
builder.Services.AddIdentityModule(ConnectionString);
builder.Services.AddAuditModule(ConnectionString);
builder.Services.AddDocumentsModule(ConnectionString);
builder.Services.AddVendorsModule(ConnectionString);
builder.Services.AddInvoicesModule(ConnectionString);
builder.Services.AddWorkflowModule(ConnectionString);
builder.Services.AddPaymentsModule(ConnectionString);
builder.Services.AddErpIntegrationModule(ConnectionString);
builder.Services.AddReportingModule();
builder.Services.AddNotificationsModule(ConnectionString, o => o.BaseUrl = builder.Configuration["App:BaseUrl"] ?? "");
builder.Services.AddD365FinanceAdapter(builder.Configuration);
// The mock ERP is for local development only; production tenants cannot select a provider that is not registered.
if (builder.Environment.IsDevelopment()) builder.Services.AddSingleton<Vpms.ErpIntegration.Contracts.IErpAdapter, Vpms.ErpIntegration.Infrastructure.MockErpAdapter>();
// Country tax rule plug-ins for received invoices. Removing a line switches that country back to the standard checks.
builder.Services.AddSingleton<Vpms.Invoices.Domain.Tax.ITaxRuleProvider, Vpms.Tax.Sa.SaudiVatProvider>();
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.Section));
builder.Services.AddVendorPortalModule(ConnectionString);
builder.Services.AddSingleton<IAuditAnchorSink>(sp => new FileAuditAnchorSink(
    sp.GetRequiredService<IConfiguration>()["Audit:AnchorDirectory"] ?? Path.Combine(Path.GetTempPath(), "vpms-audit-anchors")));
builder.Services.AddMemoryCache();

// Per-tenant OIDC schemes are added at runtime; the shared post-configuration (data protection, nonce/state formats) is registered once.
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());
builder.Services.AddSingleton<OidcBackchannelOptions>();
builder.Services.AddSingleton<DynamicOidcSchemes>();
builder.Services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();

builder.Services.AddAuthentication(VpmsAuth.CookieScheme)
    .AddCookie(VpmsAuth.CookieScheme, o =>
    {
        o.Cookie.Name = "__Host-vpms";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        // Lax (not Strict): the IdP redirect back to us is a cross-site navigation and must carry the new session.
        // State-changing requests are protected by antiforgery tokens instead.
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.Path = "/";
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = CookieSessionValidator.ValidateAsync;
        // APIs answer 401/403 rather than redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });

builder.Services.AddAuthentication()
    .AddCookie(VpmsAuth.VendorCookieScheme, o =>
    {
        // A separate cookie, scheme and context: a vendor session can never satisfy a staff policy, or the other way round.
        o.Cookie.Name = "__Host-vpms-vendor";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.Path = "/";
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(20);
        o.Events.OnValidatePrincipal = VendorCookieValidator.ValidateAsync;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });

builder.Services.AddOptions<CookieAuthenticationOptions>(VpmsAuth.CookieScheme)
    .Configure<IOptions<AuthOptions>>((cookie, auth) => cookie.ExpireTimeSpan = auth.Value.IdleTimeout);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(VpmsAuth.InternalPolicy, p => p
        .RequireAuthenticatedUser()
        .RequireClaim(VpmsClaims.Context, VpmsClaims.InternalContext)
        .RequireClaim(VpmsClaims.TenantId))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(VpmsClaims.Context, VpmsClaims.InternalContext)
        .RequireClaim(VpmsClaims.TenantId)
        .Build())
    .AddPolicy(VendorClaims.PortalPolicy, p => p
        .AddAuthenticationSchemes(VpmsAuth.VendorCookieScheme)
        .RequireAuthenticatedUser()
        .RequireClaim(VpmsClaims.Context, VendorClaims.VendorContext)
        .RequireClaim(VendorClaims.VendorId)
        .RequireClaim(VpmsClaims.TenantId))
    .AddPermissionPolicies();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = "__Host-vpms-csrf";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsCustomization.Customize);
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddVpmsRateLimiting();
builder.Services.AddVpmsTelemetry(builder.Configuration);
builder.Services.AddVpmsHealthChecks();

builder.Services.AddControllers();
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.ReportApiVersions = true;
    o.ApiVersionReader = new UrlSegmentApiVersionReader();
}).AddMvc();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsProduction()
    && string.Equals(app.Configuration["Encryption:Provider"] ?? "Local", "Local", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Encryption:Provider must be KeyVault in Production; the Local key is for development only.");
}

if (app.Environment.IsProduction()
    && string.Equals(app.Configuration["Storage:Provider"] ?? "InMemory", "InMemory", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Storage:Provider must be Azure or S3 in Production.");
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseMiddleware<AuditContextMiddleware>();
app.UseMiddleware<OidcCallbackSchemeMiddleware>();
app.UseAuthentication();
app.UseMiddleware<VendorSessionMiddleware>();
app.UseMiddleware<TenantContextMiddleware>();
app.UseMiddleware<AuditActorMiddleware>();
app.UseMiddleware<LogEnrichmentMiddleware>();
app.UseAuthorization();
app.UseMiddleware<CsrfProtectionMiddleware>();

if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Testing")
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapVpmsHealth();
app.MapAuthEndpoints();
app.MapControllers();

app.Run();

public partial class Program;
