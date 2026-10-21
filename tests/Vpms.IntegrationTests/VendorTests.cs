using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Documents.Application;
using Vpms.Documents.Contracts;
using Vpms.TestSupport;
using Vpms.Vendors.Application;
using Vpms.Vendors.Contracts;
using Vpms.Vendors.Domain;
using Vpms.Vendors.Infrastructure;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class VendorTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _maker = Guid.NewGuid();
    private readonly Guid _checker = Guid.NewGuid();

    public VendorTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(await _sql.CreateDatabaseAsync());
        foreach (var t in new[] { _tenantA, _tenantB })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<Vpms.BuildingBlocks.Tenancy.TenantContext>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            db.Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Vendors_are_created_with_encrypted_tax_data_and_audited()
    {
        var v = await Create(_tenantA, "Al Amal Trading", "300012345600003");
        Assert.Equal(VendorStatus.Invited, v.Status);
        Assert.Equal("300012345600003", v.TaxId);

        await using var scope = _host.Scope(_tenantA);
        var row = await scope.ServiceProvider.GetRequiredService<VendorsDbContext>().Vendors.AsNoTracking().SingleAsync();
        Assert.StartsWith("v1.", row.TaxIdCipher);
        Assert.DoesNotContain("30001234", row.TaxIdCipher, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, row.CreatedBy);

        var events = (await scope.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(EntityType: "Vendor", Limit: 20), null)).Items;
        Assert.Equal("vendor.created", Assert.Single(events).Action);
        Assert.DoesNotContain("3000123", events[0].ChangeSummaryJson ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_vendor_with_the_same_tax_number_is_a_duplicate_unless_confirmed()
    {
        await Create(_tenantA, "First", "300012345600003");
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Create(_tenantA, "Second", "3000-1234-5600003"));
        Assert.Equal("vendor_duplicate", ex.Code);
        await Create(_tenantA, "Second", "3000 1234 5600003", confirm: true);
        await Create(_tenantB, "Other tenant", "300012345600003"); // never a duplicate across customers
    }

    [Theory]
    [InlineData("", "SA", "invalid_name")]
    [InlineData("Name", "SAU", "invalid_country")]
    public async Task Invalid_vendors_are_rejected(string name, string country, string code)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>()
            .CreateAsync(new CreateVendor(name, null, null, country, null, null, null, null))));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Approval_needs_verified_clean_required_documents_and_a_different_person()
    {
        var v = await Create(_tenantA, "Vendor One", "300012345600003");
        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().SubmitAsync(v.Id));
        await Run(_tenantA, _checker, s => s.GetRequiredService<IVendorService>().StartReviewAsync(v.Id));

        var missing = await Assert.ThrowsAsync<ConflictException>(() => Run(_tenantA, _checker, s => s.GetRequiredService<IVendorService>().ApproveAsync(v.Id)));
        Assert.Equal("documents_missing", missing.Code);
        Assert.Contains("CommercialRegistration", missing.Message);

        var docs = new[] { VendorDocumentType.CommercialRegistration, VendorDocumentType.TaxCertificate };
        var added = new List<VendorDocumentDto>();
        foreach (var type in docs) added.Add(await AddDocument(_tenantA, v.Id, type, _maker));

        // not yet scanned or verified
        await Assert.ThrowsAsync<ConflictException>(() => Run(_tenantA, _checker, s => s.GetRequiredService<IVendorService>().ApproveAsync(v.Id)));
        await ScanAll();
        foreach (var d in added) await Run(_tenantA, _checker, s => s.GetRequiredService<IVendorDocumentService>().VerifyAsync(d.Id));

        // the maker (creator) is blocked even with everything in place
        var sod = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().ApproveAsync(v.Id)));
        Assert.Equal("different-person", sod.Rule);

        var approved = await Run(_tenantA, _checker, s => s.GetRequiredService<IVendorService>().ApproveAsync(v.Id));
        Assert.Equal(VendorStatus.Active, approved.Status);

        await using var scope = _host.Scope(_tenantA);
        var actions = (await scope.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Limit: 100), null)).Items.Select(e => e.Action).ToList();
        Assert.Contains("sod.vendor.blocked", actions);
        Assert.Contains("vendor.approved", actions);
    }

    [Fact]
    public async Task Rejected_or_expired_documents_do_not_count()
    {
        var v = await ReadyForReview(_tenantA, "Vendor Two", verifyDocs: false);
        var docs = await Run(_tenantA, _checker, s => s.GetRequiredService<IVendorDocumentService>().ListAsync(v));
        await Run(_tenantA, _checker, s => s.GetRequiredService<IVendorDocumentService>().VerifyAsync(docs[0].Id));
        await Run(_tenantA, _checker, s => s.GetRequiredService<IVendorDocumentService>().RejectAsync(docs[1].Id, "Illegible"));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Run(_tenantA, _checker, s => s.GetRequiredService<IVendorService>().ApproveAsync(v)));
        Assert.Equal("documents_missing", ex.Code);
    }

    [Fact]
    public async Task Documents_must_belong_to_the_vendor_and_a_file_can_be_used_once()
    {
        var v1 = await Create(_tenantA, "V1", null);
        var v2 = await Create(_tenantA, "V2", null);
        var doc = await AddDocument(_tenantA, v1.Id, VendorDocumentType.TaxCertificate, _maker);

        var other = await Upload(_tenantA, v2.Id);
        Assert.Equal("attachment_mismatch", (await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Run(_tenantA, _maker, s => s.GetRequiredService<IVendorDocumentService>().AddAsync(v1.Id, VendorDocumentType.Other, other.Id, null)))).Code);
        Assert.Equal("document_exists", (await Assert.ThrowsAsync<ConflictException>(() =>
            Run(_tenantA, _maker, s => s.GetRequiredService<IVendorDocumentService>().AddAsync(v1.Id, VendorDocumentType.Other, doc.AttachmentId, null)))).Code);
        Assert.Equal("invalid_document_type", (await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Run(_tenantA, _maker, s => s.GetRequiredService<IVendorDocumentService>().AddAsync(v1.Id, "Passport", other.Id, null)))).Code);
    }

    [Fact]
    public async Task Pending_documents_cannot_be_verified_before_the_scan()
    {
        var v = await Create(_tenantA, "V", null);
        var doc = await AddDocument(_tenantA, v.Id, VendorDocumentType.TaxCertificate, _maker);
        Assert.Equal("document_not_clean", (await Assert.ThrowsAsync<ConflictException>(() =>
            Run(_tenantA, _checker, s => s.GetRequiredService<IVendorDocumentService>().VerifyAsync(doc.Id)))).Code);
    }

    [Fact]
    public async Task The_same_vendor_has_separate_accounting_rules_in_each_legal_entity()
    {
        var v = await Create(_tenantA, "Shared Vendor", null);
        var e1 = await Entity(_tenantA, "E1", "SAR");
        var e2 = await Entity(_tenantA, "E2", "AED");

        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().SaveAsync(v.Id, e1,
            new SaveVendorEntityLink("V-100", "NET30", "SAR", "VAT15", "{\"costCenter\":\"10\"}", "WHT0")));
        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().SaveAsync(v.Id, e2,
            new SaveVendorEntityLink("AE-77", "NET60", "AED", "VAT5", null, null)));

        var links = await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().ListAsync(v.Id, null));
        Assert.Equal(2, links.Count);
        Assert.Equal(("NET30", "SAR"), (links.Single(l => l.LegalEntityId == e1).PaymentTermsCode, links.Single(l => l.LegalEntityId == e1).DefaultCurrency));
        Assert.Equal(("NET60", "AED"), (links.Single(l => l.LegalEntityId == e2).PaymentTermsCode, links.Single(l => l.LegalEntityId == e2).DefaultCurrency));

        var scoped = await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().ListAsync(v.Id, new HashSet<Guid> { e2 }));
        Assert.Equal([e2], scoped.Select(l => l.LegalEntityId));

        var updated = await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().SaveAsync(v.Id, e1,
            new SaveVendorEntityLink("V-100", "NET45", "SAR", "VAT15", null, null)));
        Assert.Equal("NET45", updated.PaymentTermsCode);
        Assert.Equal(2, (await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().ListAsync(v.Id, null))).Count);
    }

    [Fact]
    public async Task Links_validate_entity_currency_and_json_and_never_cross_tenants()
    {
        var v = await Create(_tenantA, "V", null);
        var e = await Entity(_tenantA, "E1", "SAR");
        var eB = await Entity(_tenantB, "B1", "SAR");

        Assert.Equal("invalid_currency", (await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>()
            .SaveAsync(v.Id, e, new SaveVendorEntityLink(null, null, "sar", null, null, null))))).Code);
        Assert.Equal("invalid_json", (await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>()
            .SaveAsync(v.Id, e, new SaveVendorEntityLink(null, null, "SAR", null, "[1]", null))))).Code);
        Assert.Equal("unknown_legal_entity", (await Assert.ThrowsAsync<ValidationFailedException>(() => Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>()
            .SaveAsync(v.Id, eB, new SaveVendorEntityLink(null, null, "SAR", null, null, null))))).Code);
    }

    [Fact]
    public async Task Only_active_vendors_with_an_unblocked_link_can_transact()
    {
        var v = await ReadyForReview(_tenantA, "Transacting");
        var e = await Entity(_tenantA, "E1", "SAR");
        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().SaveAsync(v, e, new SaveVendorEntityLink(null, null, "SAR", null, null, null)));

        async Task<bool> Can() => await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorLookup>().CanTransactAsync(v, e));
        Assert.False(await Can(), "not approved yet");

        await Run(_tenantA, _checker, s => s.GetRequiredService<IVendorService>().ApproveAsync(v));
        Assert.True(await Can());

        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().SetBlockedAsync(v, e, true));
        Assert.False(await Can());
        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().SetBlockedAsync(v, e, false));
        Assert.True(await Can());

        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().SuspendAsync(v, "Under investigation"));
        Assert.False(await Can());
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().ReactivateAsync(v)));
        await Run(_tenantA, _checker, s => s.GetRequiredService<IVendorService>().ReactivateAsync(v));
        Assert.True(await Can());
    }

    [Fact]
    public async Task Other_tenants_cannot_see_vendors_documents_or_links()
    {
        var v = await Create(_tenantA, "Private Vendor", "300012345600003");
        await Assert.ThrowsAsync<NotFoundException>(() => Run(_tenantB, _maker, s => s.GetRequiredService<IVendorService>().GetAsync(v.Id)));
        Assert.Empty(await Run(_tenantB, _maker, s => s.GetRequiredService<IVendorService>().ListAsync(null, null, null, 0, 50)));
        Assert.Null(await Run(_tenantB, _maker, s => s.GetRequiredService<IVendorLookup>().FindAsync(v.Id)));
    }

    [Fact]
    public async Task Lists_filter_by_status_search_and_entity_scope()
    {
        var a = await Create(_tenantA, "Alpha Supplies", null);
        var b = await Create(_tenantA, "Beta Logistics", null);
        var e = await Entity(_tenantA, "E1", "SAR");
        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorEntityLinkService>().SaveAsync(a.Id, e, new SaveVendorEntityLink(null, null, "SAR", null, null, null)));
        await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().SubmitAsync(b.Id));

        Assert.Equal(["Alpha Supplies"], (await List(null, "alpha")).Select(x => x.LegalName));
        Assert.Equal(["Beta Logistics"], (await List("Submitted", null)).Select(x => x.LegalName));
        Assert.Equal(["Alpha Supplies"], (await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().ListAsync(null, null, new HashSet<Guid> { e }, 0, 50))).Select(x => x.LegalName));
        Assert.Empty(await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().ListAsync(null, null, new HashSet<Guid> { Guid.NewGuid() }, 0, 50)));

        Task<IReadOnlyList<VendorListItem>> List(string? status, string? search) =>
            Run(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().ListAsync(status, search, null, 0, 50));
    }

    [Fact]
    public async Task Requests_and_replies_form_a_thread()
    {
        var v = await Create(_tenantA, "Chatty", null);
        var r = await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorRequestService>().CreateAsync(v.Id, "Updated certificate", "Please send the new tax certificate."));
        Assert.Equal(("Open", 1), (r.Status, r.Messages.Count));
        r = await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorRequestService>().ReplyAsync(r.Id, "Reminder: due Friday."));
        Assert.Equal(2, r.Messages.Count);
        r = await Run(_tenantA, _maker, s => s.GetRequiredService<IVendorRequestService>().CloseAsync(r.Id));
        Assert.Equal("Closed", r.Status);
        Assert.Equal("request_closed", (await Assert.ThrowsAsync<ConflictException>(() => Run(_tenantA, _maker, s => s.GetRequiredService<IVendorRequestService>().ReplyAsync(r.Id, "again")))).Code);
    }

    [Fact]
    public async Task Concurrent_edits_are_detected_not_overwritten()
    {
        var v = await Create(_tenantA, "Contended", null);
        await using var s1 = _host.Scope(_tenantA, _maker);
        await using var s2 = _host.Scope(_tenantA, _checker);
        var db1 = s1.ServiceProvider.GetRequiredService<VendorsDbContext>();
        var db2 = s2.ServiceProvider.GetRequiredService<VendorsDbContext>();
        var a = await db1.Vendors.SingleAsync(x => x.Id == v.Id);
        var b = await db2.Vendors.SingleAsync(x => x.Id == v.Id);
        a.LegalName = "First writer";
        await db1.SaveChangesAsync();
        b.LegalName = "Second writer";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private async Task<T> Run<T>(Guid tenant, Guid actor, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _host.Scope(tenant, actor);
        return await action(scope.ServiceProvider);
    }

    private Task<VendorDto> Create(Guid tenant, string name, string? taxId, bool confirm = false) =>
        Run(tenant, _maker, s => s.GetRequiredService<IVendorService>().CreateAsync(new CreateVendor(name, null, null, "SA", taxId, null, null, null, confirm)));

    private async Task<Guid> Entity(Guid tenant, string code, string currency) =>
        (await Run(tenant, _maker, s => s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity(code, code, null, "SA", currency, null)))).Id;

    private Task<AttachmentInfo> Upload(Guid tenant, Guid vendorId, Guid? actor = null) =>
        Run(tenant, actor ?? _maker, s => s.GetRequiredService<IAttachmentService>()
            .UploadAsync(new UploadRequest("Vendor", vendorId.ToString(), null, "doc.pdf", new MemoryStream(Pdf), actor ?? _maker, UploaderKind.Staff)));

    private async Task<VendorDocumentDto> AddDocument(Guid tenant, Guid vendorId, string type, Guid actor)
    {
        var upload = await Upload(tenant, vendorId, actor);
        return await Run(tenant, actor, s => s.GetRequiredService<IVendorDocumentService>().AddAsync(vendorId, type, upload.Id, null));
    }

    /// <summary>Creates a vendor and gets it to UnderReview with both required documents uploaded, scanned and (optionally) verified.</summary>
    private async Task<Guid> ReadyForReview(Guid tenant, string name, bool verifyDocs = true)
    {
        var v = await Create(tenant, name, null);
        var docs = new List<VendorDocumentDto>();
        foreach (var type in VendorDocumentType.RequiredForApproval) docs.Add(await AddDocument(tenant, v.Id, type, _maker));
        await ScanAll();
        await Run(tenant, _maker, s => s.GetRequiredService<IVendorService>().SubmitAsync(v.Id));
        await Run(tenant, _checker, s => s.GetRequiredService<IVendorService>().StartReviewAsync(v.Id));
        if (verifyDocs)
        {
            foreach (var d in docs) await Run(tenant, _checker, s => s.GetRequiredService<IVendorDocumentService>().VerifyAsync(d.Id));
        }

        return v.Id;
    }

    private async Task ScanAll()
    {
        IReadOnlyList<PendingScan> pending;
        await using (var list = _host.Scope(null))
        {
            pending = await list.ServiceProvider.GetRequiredService<IAttachmentScanService>().ListPendingAsync(100);
        }

        foreach (var item in pending)
        {
            await using var scope = _host.Scope(item.TenantId);
            await scope.ServiceProvider.GetRequiredService<IAttachmentScanService>().ScanAsync(item.AttachmentId);
        }
    }
}
