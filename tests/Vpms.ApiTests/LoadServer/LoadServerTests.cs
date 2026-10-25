using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Identity.Domain;
using Vpms.Identity.Infrastructure;
using Vpms.Invoices.Application;
using Vpms.TestSupport;

namespace Vpms.ApiTests.LoadServer;

/// <summary>
/// Not a test of behaviour: when VPMS_LOAD_SERVE=1 it seeds a tenant with realistic volume, serves the real API over HTTPS for the k6 scenarios in
/// <c>loadtests/</c>, and stops when the stop file appears (or after VPMS_LOAD_SECONDS). Otherwise it does nothing. See docs/performance.md.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LoadServerTests(SqlServerFixture sql)
{
    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;

    [Fact]
    public async Task Serve_the_api_for_load_tests()
    {
        if (Environment.GetEnvironmentVariable("VPMS_LOAD_SERVE") != "1") return;

        var port = Env("VPMS_LOAD_PORT", 5443);
        var invoices = Env("VPMS_LOAD_INVOICES", 3000);
        var ready = Environment.GetEnvironmentVariable("VPMS_LOAD_READY_FILE") ?? "/tmp/claude-0/load.ready";
        var stop = Environment.GetEnvironmentVariable("VPMS_LOAD_STOP_FILE") ?? "/tmp/claude-0/load.stop";
        File.Delete(ready);
        File.Delete(stop);

        var api = new LoadApiFixture(sql, port);
        await api.InitializeAsync();
        try
        {
            var root = api.Services; // creates the host and starts Kestrel
            var tenant = api.TenantA;
            var admin = await CreatePersonaAsync(api, "admin", "Load Admin", "Tenant Administrator", "Auditor", "Report Viewer");
            var clerk = await CreatePersonaAsync(api, "clerk", "Load Clerk", "Invoice Clerk", "Report Viewer", "Auditor");
            await CreatePersonaAsync(api, "auditor", "Load Auditor", "Auditor", "Report Viewer");
            var maker = await CreatePersonaAsync(api, "maker", "Vendor Maker");
            var checker = await CreatePersonaAsync(api, "checker", "Vendor Checker");

            var entity = await ActiveVendorKit.CreateEntityAsync(root, tenant, admin, "LOAD1");
            var vendors = new List<Guid>();
            for (var i = 0; i < Env("VPMS_LOAD_VENDORS", 5); i++) vendors.Add(await ActiveVendorKit.ActiveVendorAsync(root, tenant, entity, $"Load Vendor {i:D3}", maker, checker));

            var created = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, invoices), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, ct) =>
            {
                var net = 100m + (i % 50) * 10m;
                var tax = Math.Round(net * 0.15m, 2);
                var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-(i % 400)));
                var data = new InvoiceData($"LOAD-{i:D6}", date, date.AddDays(30), "SAR", net, tax, net + tax, null, null, [new InvoiceLineInput("Consulting", 1, net, net, "S", 0.15m, tax, null, null)]);
                await ActiveVendorKit.AsAsync(root, tenant, clerk, async s =>
                {
                    var invoice = await s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(entity, vendors[i % vendors.Count], data), ct);
                    if (i % 3 == 0) await s.GetRequiredService<IInvoiceService>().SubmitAsync(invoice.Id, ct);
                    return 0;
                });
                Interlocked.Increment(ref created);
            });

            await File.WriteAllTextAsync(ready, JsonSerializer.Serialize(new { baseUrl = $"https://localhost:{port}", entity, invoices = created, vendors = vendors.Count }));
            var deadline = DateTime.UtcNow.AddSeconds(Env("VPMS_LOAD_SECONDS", 3600));
            while (!File.Exists(stop) && DateTime.UtcNow < deadline) await Task.Delay(500);
        }
        finally
        {
            await api.DisposeAsync();
        }
    }

    private static async Task<Guid> CreatePersonaAsync(LoadApiFixture api, string persona, string name, params string[] roles)
    {
        var id = Guid.NewGuid();
        var email = $"{persona}@acme.com";
        await ActiveVendorKit.AsAsync(api.Services, api.TenantA, id, async s =>
        {
            await s.GetRequiredService<Vpms.Identity.Application.IRoleAssignmentService>().EnsureSystemRolesAsync();
            var db = s.GetRequiredService<IdentityDbContext>();
            db.Users.Add(new User { Id = id, ProviderId = api.ProviderId, ExternalSubject = "load-" + persona, Email = email, DisplayName = name, CreatedAt = DateTimeOffset.UtcNow });
            foreach (var role in roles)
            {
                var r = await db.Roles.FirstAsync(x => x.Name == role);
                db.RoleAssignments.Add(new RoleAssignment { Id = Guid.NewGuid(), UserId = id, RoleId = r.Id, AssignedAt = DateTimeOffset.UtcNow, AssignedBy = id });
            }

            await db.SaveChangesAsync();
            return 0;
        });
        api.Personas[persona] = (id, email, name);
        return id;
    }
}
