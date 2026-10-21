using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Storage;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Documents.Application;
using Vpms.Documents.Contracts;
using Vpms.Documents.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AttachmentTests : IAsyncLifetime
{
    private const string Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");

    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _entity = Guid.NewGuid();

    public AttachmentTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _host = new IntegrationHost(await _sql.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Uploads_are_quarantined_until_scanned_clean()
    {
        var info = await Upload(_tenantA, "invoice.pdf", Pdf);
        Assert.Equal(ScanStatus.Pending, info.ScanStatus);
        Assert.Equal("application/pdf", info.ContentType);
        Assert.Equal(Pdf.Length, info.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Pdf)), info.Sha256);

        await using (var scope = _host.Scope(_tenantA))
        {
            var ex = await Assert.ThrowsAsync<ConflictException>(() => scope.ServiceProvider.GetRequiredService<IAttachmentService>().OpenDownloadAsync(info.Id));
            Assert.Equal("attachment_not_ready", ex.Code);
        }

        await ScanAll();

        await using var after = _host.Scope(_tenantA);
        var svc = after.ServiceProvider.GetRequiredService<IAttachmentService>();
        Assert.Equal(ScanStatus.Clean, (await svc.GetAsync(info.Id)).ScanStatus);
        var download = await svc.OpenDownloadAsync(info.Id);
        using var ms = new MemoryStream();
        await download.Content.CopyToAsync(ms);
        Assert.Equal(Pdf, ms.ToArray());
        Assert.Equal("invoice.pdf", download.FileName);
    }

    [Fact]
    public async Task Infected_files_are_destroyed_and_never_downloadable()
    {
        var info = await Upload(_tenantA, "invoice.pdf", [.. Pdf, .. Encoding.ASCII.GetBytes(Eicar)]);
        await ScanAll();

        await using var scope = _host.Scope(_tenantA);
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentService>();
        var got = await svc.GetAsync(info.Id);
        Assert.Equal(ScanStatus.Infected, got.ScanStatus);
        await Assert.ThrowsAsync<ConflictException>(() => svc.OpenDownloadAsync(info.Id));

        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var row = await db.Attachments.AsNoTracking().SingleAsync(a => a.Id == info.Id);
        Assert.Equal("Eicar-Test-Signature", row.ScanSignature);
        // the infected content is deleted from storage
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IFileStore>().GetAsync(row.StorageKey));
    }

    [Fact]
    public async Task A_scanner_that_keeps_failing_leaves_the_file_unavailable_and_ends_in_Failed()
    {
        await using var host = new IntegrationHost(await _sql.CreateDatabaseAsync(), configure: s =>
            s.AddSingleton<IVirusScanner>(new ThrowingScanner()));
        await using (var scope = host.Scope(_tenantA))
        {
            await scope.ServiceProvider.GetRequiredService<IAttachmentService>()
                .UploadAsync(new UploadRequest("Vendor", "v1", null, "a.pdf", new MemoryStream(Pdf), null, UploaderKind.Staff));
        }

        for (var i = 0; i < 4; i++)
        {
            await ScanAll(host);
        }

        await using var read = host.Scope(_tenantA);
        var row = await read.ServiceProvider.GetRequiredService<DocumentsDbContext>().Attachments.AsNoTracking().SingleAsync();
        Assert.Equal(ScanStatus.Failed, row.ScanStatus);
        Assert.Equal(3, row.ScanAttempts);
        await Assert.ThrowsAsync<ConflictException>(() => read.ServiceProvider.GetRequiredService<IAttachmentService>().OpenDownloadAsync(row.Id));
    }

    [Fact]
    public async Task Invalid_uploads_are_rejected_before_anything_is_stored()
    {
        await AssertRejected("virus.exe", Pdf, "unsupported_type", 415);
        await AssertRejected("fake.pdf", "MZ-not-a-pdf"u8.ToArray(), "content_mismatch", 400);
        await AssertRejected("empty.pdf", [], "empty_file", 400);

        await using var host = new IntegrationHost(await _sql.CreateDatabaseAsync(), configure: s =>
            s.Configure<DocumentsOptions>(o => { o.Scanner = "Eicar"; o.MaxBytes = 1024; }));
        await using var scope = host.Scope(_tenantA);
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => scope.ServiceProvider.GetRequiredService<IAttachmentService>()
            .UploadAsync(new UploadRequest("Vendor", "v1", null, "big.pdf", new MemoryStream([.. Pdf, .. new byte[2000]]), null, UploaderKind.Staff)));
        Assert.Equal("file_too_large", ex.Code);
        Assert.Equal(413, ex.StatusCode);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().Attachments.ToListAsync());
    }

    [Fact]
    public async Task Hostile_file_names_never_reach_the_storage_key()
    {
        var info = await Upload(_tenantA, "..\\..\\..\\etc\\passwd.pdf", Pdf);
        Assert.Equal("passwd.pdf", info.FileName);

        await using var scope = _host.Scope(_tenantA);
        var row = await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().Attachments.AsNoTracking().SingleAsync(a => a.Id == info.Id);
        Assert.StartsWith($"{_tenantA:N}/", row.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain("..", row.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_tenants_cannot_see_download_or_reach_the_file()
    {
        var info = await Upload(_tenantA, "invoice.pdf", Pdf);
        await ScanAll();

        await using var scope = _host.Scope(_tenantB);
        var svc = scope.ServiceProvider.GetRequiredService<IAttachmentService>();
        await Assert.ThrowsAsync<NotFoundException>(() => svc.GetAsync(info.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.OpenDownloadAsync(info.Id));
        Assert.Empty(await svc.ListForOwnerAsync("Vendor", "v1"));

        string key;
        await using (var a = _host.Scope(_tenantA))
        {
            key = (await a.ServiceProvider.GetRequiredService<DocumentsDbContext>().Attachments.AsNoTracking().SingleAsync()).StorageKey;
        }

        await Assert.ThrowsAsync<TenantViolationException>(() => scope.ServiceProvider.GetRequiredService<IFileStore>().GetAsync(key));
    }

    [Fact]
    public async Task Upload_scan_and_download_are_audited_and_the_chain_stays_valid()
    {
        var info = await Upload(_tenantA, "invoice.pdf", Pdf);
        await ScanAll();
        await using (var scope = _host.Scope(_tenantA))
        {
            await (await scope.ServiceProvider.GetRequiredService<IAttachmentService>().OpenDownloadAsync(info.Id)).Content.DisposeAsync();
        }

        await using var read = _host.Scope(_tenantA);
        var events = (await read.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(EntityType: "Attachment", Limit: 50), null)).Items;
        Assert.Equal(["attachment.uploaded", "attachment.scan.clean", "attachment.downloaded"], events.Select(e => e.Action));
        Assert.Equal(_entity, events[0].LegalEntityId);

        await using var verify = _host.Scope(null);
        Assert.True((await verify.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(_tenantA)).IsValid);
    }

    [Fact]
    public async Task Files_are_listed_per_owner()
    {
        await Upload(_tenantA, "a.pdf", Pdf, ownerId: "v1");
        await Upload(_tenantA, "b.pdf", Pdf, ownerId: "v2");

        await using var scope = _host.Scope(_tenantA);
        var list = await scope.ServiceProvider.GetRequiredService<IAttachmentService>().ListForOwnerAsync("Vendor", "v1");
        Assert.Equal(["a.pdf"], list.Select(a => a.FileName));
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private async Task<AttachmentInfo> Upload(Guid tenant, string name, byte[] content, string ownerId = "v1")
    {
        await using var scope = _host.Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<IAttachmentService>()
            .UploadAsync(new UploadRequest("Vendor", ownerId, _entity, name, new MemoryStream(content), Guid.NewGuid(), UploaderKind.Staff));
    }

    private async Task AssertRejected(string name, byte[] content, string code, int status)
    {
        await using var scope = _host.Scope(_tenantA);
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => scope.ServiceProvider.GetRequiredService<IAttachmentService>()
            .UploadAsync(new UploadRequest("Vendor", "v1", null, name, new MemoryStream(content), null, UploaderKind.Staff)));
        Assert.Equal(code, ex.Code);
        Assert.Equal(status, ex.StatusCode);
    }

    private async Task ScanAll(IntegrationHost? host = null)
    {
        host ??= _host;
        IReadOnlyList<PendingScan> pending;
        await using (var list = host.Scope(null))
        {
            pending = await list.ServiceProvider.GetRequiredService<IAttachmentScanService>().ListPendingAsync(100);
        }

        foreach (var item in pending)
        {
            await using var scope = host.Scope(item.TenantId);
            await scope.ServiceProvider.GetRequiredService<IAttachmentScanService>().ScanAsync(item.AttachmentId);
        }
    }

    private sealed class ThrowingScanner : IVirusScanner
    {
        public Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default) => throw new IOException("scanner down");
    }
}
