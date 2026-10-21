using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Vpms.Api.Auth;
using Vpms.BuildingBlocks.Persistence;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Identity.Application;
using Vpms.Identity.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

static string ConnectionString(IServiceProvider sp) =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddTenancy();
builder.Services.AddCustomerAdminModule(ConnectionString);
builder.Services.AddIdentityModule(ConnectionString);
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
        o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        o.Events.OnValidatePrincipal = CookieSessionValidator.ValidateAsync;
        // APIs answer 401/403 rather than redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });

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

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseHttpsRedirection();
app.UseMiddleware<OidcCallbackSchemeMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();
app.UseMiddleware<CsrfProtectionMiddleware>();

app.MapAuthEndpoints();
app.MapControllers();

app.Run();

public partial class Program;
