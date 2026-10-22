using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Documents.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class InvoiceTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private TestDatabase _db = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _clerk = Guid.NewGuid();
    private readonly Guid _maker = Guid.NewGuid();
    private readonly Guid _checker = Guid.NewGuid();
    private Guid _entityA, _entityB, _vendor, _otherVendor;

    public InvoiceTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _db = await _sql.CreateDatabaseAsync();
        _host = new IntegrationHost(_db);
        foreach (var t in new[] { _tenantA, _tenantB })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var admin = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            admin.Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await admin.SaveChangesAsync();
        }

        _entityA = await ActiveVendorKit.CreateEntityAsync(_host.Services, _tenantA, _maker, "KSA");
        _entityB = await ActiveVendorKit.CreateEntityAsync(_host.Services, _tenantA, _maker, "UAE", "AE", "AED");
        _vendor = await ActiveVendorKit.ActiveVendorAsync(_host.Services, _tenantA, _entityA, "Al Amal Trading", _maker, _checker);
        _otherVendor = await ActiveVendorKit.ActiveVendorAsync(_host.Services, _tenantA, _entityA, "Other Supplier", _maker, _checker);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static InvoiceLineInput Line(decimal amount = 100m, string desc = "Consulting") => new(desc, 1, amount, amount, "S", 0.15m, Math.Round(amount * 0.15m, 2), null, null);

    private static InvoiceData Data(string number = "INV-001", decimal net = 100m) =>
        new(number, TestDates.Ago(10), TestDates.Ago(10).AddDays(30), "SAR", net, Math.Round(net * 0.15m, 2), net + Math.Round(net * 0.15m, 2), null, "ZATCA-UUID-1", [Line(net)]);

    private Task<T> As<T>(Guid tenant, Guid actor, Func<IServiceProvider, Task<T>> action) => ActiveVendorKit.AsAsync(_host.Services, tenant, actor, action);

    private Task<InvoiceDto> Create(string number = "INV-001", Guid? vendor = null, Guid? entity = null, InvoiceData? data = null) =>
        As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(entity ?? _entityA, vendor ?? _vendor, data ?? Data(number))));

    [Fact]
    public async Task A_draft_is_created_with_lines_and_hidden_lookup_keys()
    {
        var created = await Create();
        Assert.Equal(InvoiceStatus.Draft, created.Status);
        Assert.Equal(1, created.Version);
        Assert.Equal("Al Amal Trading", created.VendorName);
        Assert.Equal(InvoiceSource.Manual, created.Source);
        var line = Assert.Single(created.Lines);
        Assert.Equal(1, line.LineNo);
        Assert.Equal(115m, created.Total);

        await using var scope = _host.Scope(_tenantA);
        var row = await scope.ServiceProvider.GetRequiredService<InvoicesDbContext>().Invoices.AsNoTracking().SingleAsync(i => i.Id == created.Id);
        Assert.Equal(64, row.DuplicateKeyIndex!.Length);
        Assert.Equal(64, row.NumberKeyIndex!.Length);
        Assert.DoesNotContain("INV", row.DuplicateKeyIndex, StringComparison.Ordinal);
        Assert.Equal(_clerk, row.CreatedBy);
    }

    [Fact]
    public async Task The_same_number_written_differently_gets_the_same_number_key_but_a_different_vendor_does_not()
    {
        var a = await Create("INV-0001");
        var b = await Create("inv/1", data: Data("inv/1"));
        var c = await Create("INV-1", vendor: _otherVendor);

        await using var scope = _host.Scope(_tenantA);
        var rows = await scope.ServiceProvider.GetRequiredService<InvoicesDbContext>().Invoices.AsNoTracking().ToDictionaryAsync(i => i.Id);
        Assert.Equal(rows[a.Id].NumberKeyIndex, rows[b.Id].NumberKeyIndex);
        Assert.Equal(rows[a.Id].DuplicateKeyIndex, rows[b.Id].DuplicateKeyIndex);
        Assert.NotEqual(rows[a.Id].NumberKeyIndex, rows[c.Id].NumberKeyIndex);
    }

    [Fact]
    public async Task Creating_an_invoice_is_audited()
    {
        var created = await Create();
        await using var scope = _host.Scope(_tenantA);
        var audit = scope.ServiceProvider.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>();
        Assert.True(await audit.Events.AnyAsync(e => e.Action == "invoice.created" && e.EntityId == created.Id.ToString() && e.ActorId == _clerk));
    }

    [Fact]
    public async Task Vendors_must_be_active_and_linked_to_the_entity()
    {
        // linked to entity A only
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Create(entity: _entityB));
        Assert.Equal("vendor_cannot_transact", ex.Code);

        // a vendor that is not approved
        var draftVendor = await As(_tenantA, _maker, async s =>
            (await s.GetRequiredService<Vpms.Vendors.Application.IVendorService>().CreateAsync(new Vpms.Vendors.Application.CreateVendor("New Co", null, null, "SA", null, null, null, null))).Id);
        await ActiveVendorKit.LinkAsync(_host.Services, _tenantA, _maker, draftVendor, _entityA);
        Assert.Equal("vendor_cannot_transact", (await Assert.ThrowsAsync<ConflictException>(() => Create(vendor: draftVendor))).Code);

        await Assert.ThrowsAsync<NotFoundException>(() => Create(vendor: Guid.NewGuid()));
        Assert.Equal("unknown_legal_entity", (await Assert.ThrowsAsync<ValidationFailedException>(() => Create(entity: Guid.NewGuid()))).Code);
    }

    [Fact]
    public async Task A_blocked_link_stops_new_invoices()
    {
        await As(_tenantA, _maker, s => s.GetRequiredService<Vpms.Vendors.Application.IVendorEntityLinkService>().SetBlockedAsync(_otherVendor, _entityA, true));
        Assert.Equal("vendor_cannot_transact", (await Assert.ThrowsAsync<ConflictException>(() => Create(vendor: _otherVendor))).Code);
    }

    [Fact]
    public async Task Drafts_can_be_edited_and_lines_are_replaced_and_renumbered()
    {
        var created = await Create();
        var edited = await As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(created.Id,
            Data("INV-002", 200m) with { Lines = [Line(120m, "A"), Line(80m, "B")] }));
        Assert.Equal(2, edited.Version);
        Assert.Equal("INV-002", edited.InvoiceNumber);
        Assert.Equal([1, 2], edited.Lines.Select(l => l.LineNo));
        Assert.Equal(["A", "B"], edited.Lines.Select(l => l.Description));

        await using var scope = _host.Scope(_tenantA);
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<InvoicesDbContext>().Lines.CountAsync(l => l.InvoiceId == created.Id));
    }

    [Fact]
    public async Task Submitted_invoices_can_no_longer_be_edited()
    {
        var created = await Create();
        var submitted = await As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id));
        Assert.Equal(InvoiceStatus.Verified, submitted.Status); // verification follows receipt at once; this invoice passes every check
        Assert.NotNull(submitted.ReceivedAt);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(created.Id, Data("INV-009"))));
        Assert.Equal("invalid_transition", ex.Code);
        await Assert.ThrowsAsync<ConflictException>(() => As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id)));
    }

    [Fact]
    public async Task Cancelling_needs_a_reason_and_is_final()
    {
        var created = await Create();
        await Assert.ThrowsAsync<ValidationFailedException>(() => As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().CancelAsync(created.Id, " ")));
        var cancelled = await As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().CancelAsync(created.Id, "Entered twice"));
        Assert.Equal(InvoiceStatus.Cancelled, cancelled.Status);
        Assert.Equal("Entered twice", cancelled.StatusReason);
        await Assert.ThrowsAsync<ConflictException>(() => As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id)));
    }

    [Fact]
    public async Task Listing_filters_by_status_vendor_search_and_entity_scope()
    {
        var a = await Create("LIST-1");
        var b = await Create("LIST-2", vendor: _otherVendor);
        await As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(b.Id));

        async Task<IReadOnlyList<InvoiceListItem>> List(InvoiceFilter f, IReadOnlySet<Guid>? scope = null) =>
            await As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().ListAsync(f, scope, 0, 50));

        Assert.Equivalent(new[] { a.Id }, (await List(new InvoiceFilter(InvoiceStatus.Draft, null, null, "LIST", null))).Select(i => i.Id));
        Assert.Equivalent(new[] { b.Id }, (await List(new InvoiceFilter(null, _otherVendor, null, null, null))).Select(i => i.Id));
        Assert.Single(await List(new InvoiceFilter(null, null, null, "LIST-2", null)));
        Assert.Empty(await List(new InvoiceFilter(null, null, null, "LIST", null), new HashSet<Guid> { _entityB }));
        Assert.Equal(2, (await List(new InvoiceFilter(null, null, null, "LIST", null), new HashSet<Guid> { _entityA })).Count);
    }

    [Fact]
    public async Task Another_tenant_sees_nothing()
    {
        var created = await Create();
        Assert.Empty(await As(_tenantB, _clerk, s => s.GetRequiredService<IInvoiceService>().ListAsync(new InvoiceFilter(null, null, null, null, null), null, 0, 50)));
        await Assert.ThrowsAsync<NotFoundException>(() => As(_tenantB, _clerk, s => s.GetRequiredService<IInvoiceService>().GetAsync(created.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => As(_tenantB, _clerk, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id)));
    }

    [Fact]
    public async Task A_vendor_session_sees_only_its_own_invoices()
    {
        var mine = await Create("V-1");
        var theirs = await Create("V-2", vendor: _otherVendor);

        await using var scope = _host.Scope(_tenantA, _clerk);
        scope.ServiceProvider.GetRequiredService<VendorContext>().SetVendor(_vendor);
        var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceService>();
        Assert.Equal([mine.Id], (await invoices.ListAsync(new InvoiceFilter(null, null, null, null, null), null, 0, 50)).Select(i => i.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => invoices.GetAsync(theirs.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => invoices.SubmitAsync(theirs.Id));
    }

    [Fact]
    public async Task Attachments_belong_to_the_invoice_and_are_served_only_when_clean()
    {
        var created = await Create();
        var other = await Create("INV-OTHER");

        var info = await UploadFor(created.Id, _entityA);
        var wrongOwner = await UploadFor(other.Id, _entityA);

        var link = await As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceAttachmentService>().AddAsync(created.Id, AttachmentRole.Original, info.Id));
        Assert.Equal(ScanStatus.Pending, link.ScanStatus);

        Assert.Equal("attachment_mismatch", (await Assert.ThrowsAsync<ValidationFailedException>(() =>
            As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceAttachmentService>().AddAsync(created.Id, AttachmentRole.Supporting, wrongOwner.Id)))).Code);
        Assert.Equal("attachment_exists", (await Assert.ThrowsAsync<ConflictException>(() =>
            As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceAttachmentService>().AddAsync(created.Id, AttachmentRole.Supporting, info.Id)))).Code);
        Assert.Equal("invalid_attachment_role", (await Assert.ThrowsAsync<ValidationFailedException>(() =>
            As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceAttachmentService>().AddAsync(other.Id, "Bogus", wrongOwner.Id)))).Code);

        await Assert.ThrowsAnyAsync<Exception>(() => As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceAttachmentService>().OpenAsync(created.Id, link.Id)));

        await ActiveVendorKit.ScanAllAsync(_host.Services);
        var download = await As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceAttachmentService>().OpenAsync(created.Id, link.Id));
        await using var ms = new MemoryStream();
        await download.Content.CopyToAsync(ms);
        Assert.Equal(Pdf, ms.ToArray());

        // the link id only works with its own invoice
        await Assert.ThrowsAsync<NotFoundException>(() => As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceAttachmentService>().OpenAsync(other.Id, link.Id)));
        var detail = await As(_tenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().GetAsync(created.Id));
        Assert.Equal(ScanStatus.Clean, Assert.Single(detail.Attachments).ScanStatus);
    }

    [Fact]
    public async Task The_application_login_cannot_delete_invoices_or_their_attachments()
    {
        var created = await Create();
        await using var conn = new SqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();
        await using (var ctx = conn.CreateCommand())
        {
            ctx.CommandText = $"EXEC sp_set_session_context N'TenantId', '{_tenantA}'";
            await ctx.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM invoice.Invoice WHERE Id = '{created.Id}'";
        var ex = await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private Task<AttachmentInfo> UploadFor(Guid invoiceId, Guid entityId) =>
        As(_tenantA, _clerk, s => s.GetRequiredService<IAttachmentService>()
            .UploadAsync(new UploadRequest("Invoice", invoiceId.ToString(), entityId, "invoice.pdf", new MemoryStream(Pdf), _clerk, UploaderKind.Staff)));
}
