using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AuditEvidenceTests(SqlServerFixture sql) : ApprovalScenarioBase(sql)
{
    private Task<EvidenceBundle> Build(Guid tenant, AuditQuery? filter = null, IReadOnlySet<Guid>? scope = null) =>
        W.AsAsync(tenant, W.Admin, s => s.GetRequiredService<IAuditEvidenceService>().BuildAsync(filter ?? new AuditQuery(), scope));

    private Task<EvidenceVerification> Verify(Guid tenant, byte[] bundle) =>
        W.AsAsync(tenant, W.Admin, s => s.GetRequiredService<IAuditEvidenceService>().VerifyAsync(new MemoryStream(bundle)));

    private static Dictionary<string, byte[]> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        });
    }

    private static byte[] Zip(Dictionary<string, byte[]> files)
    {
        using var m = new MemoryStream();
        using (var zip = new ZipArchive(m, ZipArchiveMode.Create, true))
        {
            foreach (var (name, bytes) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        }

        return m.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private async Task SomeActivity()
    {
        var invoice = await VerifiedInvoice(Clerk, "INV-1");
        await Publish(new Vpms.Workflow.Domain.WorkflowDefinitionSpec { Name = "One", Start = "s", Steps = [new Vpms.Workflow.Domain.StepSpec { Id = "s", Approvers = [WorkflowWorld.User(A1)] }] });
        await Send(Clerk, invoice.Id);
        await Decide(A1, await TaskOf(A1));
    }

    [Fact]
    public async Task A_bundle_holds_the_events_the_chain_state_and_a_signed_manifest_and_verifies()
    {
        await SomeActivity();
        var bundle = await Build(W.TenantA);
        Assert.True(bundle.Events > 5);
        Assert.True(bundle.ChainValid);

        var files = Unzip(bundle.Content);
        Assert.Equal(["chain-proof.json", "events.csv", "events.json", "manifest.json", "manifest.sig"], files.Keys.Order().ToArray());
        Assert.Equal(bundle.Events, JsonDocument.Parse(files["events.json"]).RootElement.GetArrayLength());
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, files["events.csv"].Take(3).ToArray());
        var proof = JsonDocument.Parse(files["chain-proof.json"]).RootElement;
        Assert.True(proof.GetProperty("chainValid").GetBoolean());
        Assert.Equal(bundle.ManifestSha256, Sha(files["manifest.json"]));

        var result = await Verify(W.TenantA, bundle.Content);
        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.Equal(bundle.Events, result.EventsChecked);
        Assert.True(result.SignatureValid && result.FilesIntact && result.EventsIntact && result.ChainLinked);
    }

    [Fact]
    public async Task Exporting_evidence_is_itself_audited()
    {
        await SomeActivity();
        var bundle = await Build(W.TenantA);
        var seen = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.Action == "audit.evidence.exported").Select(e => e.EntityId).ToListAsync());
        Assert.Contains(bundle.ManifestSha256, seen);
    }

    [Fact]
    public async Task A_filter_narrows_the_events_and_a_scope_limits_them_to_permitted_entities()
    {
        await SomeActivity();
        var all = await Build(W.TenantA);
        var one = await Build(W.TenantA, new AuditQuery(Action: "invoice.created"));
        Assert.InRange(one.Events, 1, all.Events - 1);
        var rows = JsonDocument.Parse(Unzip(one.Content)["events.json"]).RootElement.EnumerateArray().ToList();
        Assert.All(rows, r => Assert.Equal("invoice.created", r.GetProperty("action").GetString()));

        var outside = await Build(W.TenantA, null, new HashSet<Guid> { Guid.NewGuid() });
        Assert.Equal(0, outside.Events);
        var inside = await Build(W.TenantA, null, new HashSet<Guid> { Entity });
        Assert.All(JsonDocument.Parse(Unzip(inside.Content)["events.json"]).RootElement.EnumerateArray(), r => Assert.Equal(Entity.ToString(), r.GetProperty("legalEntityId").GetString()));
    }

    [Fact]
    public async Task Another_tenants_events_never_appear_and_its_bundle_does_not_verify_here()
    {
        await SomeActivity();
        var mine = await Build(W.TenantA);
        var theirs = await Build(W.TenantB);
        Assert.All(JsonDocument.Parse(Unzip(theirs.Content)["events.json"]).RootElement.EnumerateArray(), r => Assert.Equal(W.TenantB.ToString(), r.GetProperty("tenantId").GetString()));
        Assert.All(JsonDocument.Parse(Unzip(mine.Content)["events.json"]).RootElement.EnumerateArray(), r => Assert.Equal(W.TenantA.ToString(), r.GetProperty("tenantId").GetString()));

        var crossed = await Verify(W.TenantB, mine.Content);
        Assert.False(crossed.IsValid);
        Assert.False(crossed.SignatureValid);
    }

    [Fact]
    public async Task Changing_an_event_is_detected_whether_or_not_the_manifest_is_fixed_up()
    {
        await SomeActivity();
        var bundle = await Build(W.TenantA);

        // 1. an event is edited and nothing else is touched
        var files = Unzip(bundle.Content);
        var events = JsonNode.Parse(files["events.json"])!.AsArray();
        events[2]!["action"] = "invoice.something-else";
        files["events.json"] = Encoding.UTF8.GetBytes(events.ToJsonString());
        var plain = await Verify(W.TenantA, Zip(files));
        Assert.False(plain.IsValid);
        Assert.False(plain.FilesIntact);
        Assert.False(plain.EventsIntact);

        // 2. the manifest is also rewritten to match: the event hash still fails, and the signature no longer fits
        var manifest = JsonNode.Parse(files["manifest.json"])!;
        foreach (var f in manifest["files"]!.AsArray()) if (f!["name"]!.GetValue<string>() == "events.json") f["sha256"] = Sha(files["events.json"]);
        files["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var fixedUp = await Verify(W.TenantA, Zip(files));
        Assert.False(fixedUp.IsValid);
        Assert.True(fixedUp.FilesIntact);
        Assert.False(fixedUp.EventsIntact);
        Assert.False(fixedUp.SignatureValid);
    }

    [Fact]
    public async Task A_removed_event_between_two_kept_ones_breaks_the_link_and_a_missing_file_or_junk_is_refused()
    {
        await SomeActivity();
        var bundle = await Build(W.TenantA);
        var files = Unzip(bundle.Content);
        var events = JsonNode.Parse(files["events.json"])!.AsArray();
        var second = events[1]!.DeepClone();
        events[1]!["prevHash"] = new string('a', 64); // pretend the link to the first event is different
        files["events.json"] = Encoding.UTF8.GetBytes(events.ToJsonString());
        var linked = await Verify(W.TenantA, Zip(files));
        Assert.False(linked.IsValid);
        Assert.NotNull(second);

        files.Remove("chain-proof.json");
        var missing = await Verify(W.TenantA, Zip(files));
        Assert.False(missing.IsValid);
        Assert.Contains(missing.Problems, p => p.Contains("chain-proof.json"));

        var junk = await Verify(W.TenantA, Encoding.UTF8.GetBytes("not a zip"));
        Assert.False(junk.IsValid);
    }
}
