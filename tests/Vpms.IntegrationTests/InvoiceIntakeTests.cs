using System.Text;
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
using Vpms.Vendors.Application;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class InvoiceIntakeTests : IAsyncLifetime
{
    private const string Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");
    private readonly SqlServerFixture _sql;
    private readonly InMemoryMailboxReader _mail = new();
    private readonly SuggestingExtractor _extractor = new();
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _staff = Guid.NewGuid();
    private readonly Guid _maker = Guid.NewGuid();
    private readonly Guid _checker = Guid.NewGuid();
    private Guid _entity, _otherEntity, _vendor, _vendor2;
    private const string Inbox = "invoices@acme.example";

    public InvoiceIntakeTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(await _sql.CreateDatabaseAsync(), configure: s =>
        {
            s.AddSingleton<IMailboxReader>(_mail);
            s.AddSingleton<IDocumentExtractor>(_extractor);
        });
        foreach (var t in new[] { _tenantA, _tenantB })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var admin = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            admin.Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await admin.SaveChangesAsync();
        }

        _entity = await ActiveVendorKit.CreateEntityAsync(_host.Services, _tenantA, _maker, "KSA");
        _otherEntity = await ActiveVendorKit.CreateEntityAsync(_host.Services, _tenantA, _maker, "UAE", "AE", "AED");
        _vendor = await ActiveVendorKit.ActiveVendorAsync(_host.Services, _tenantA, _entity, "Al Amal Trading", _maker, _checker);
        _vendor2 = await ActiveVendorKit.ActiveVendorAsync(_host.Services, _tenantA, _entity, "Second Supplier", _maker, _checker);
        await SetContact(_vendor, "ap@alamal.example");
        await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().SaveMailboxAsync(_entity, Inbox, true));
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<T> As<T>(Guid tenant, Guid actor, Func<IServiceProvider, Task<T>> action) => ActiveVendorKit.AsAsync(_host.Services, tenant, actor, action);

    private async Task SetContact(Guid vendorId, string email) =>
        await As(_tenantA, _maker, s => s.GetRequiredService<IVendorService>().UpdateAsync(vendorId, new UpdateVendor("Name " + vendorId.ToString("N")[..4], null, null, null, null, email, null)));

    private static InboundMail Mail(string id, string from, params (string Name, byte[] Content)[] files) =>
        new(id, from, "Invoice " + id, DateTimeOffset.UtcNow, files.Select(f => new InboundAttachment(f.Name, "application/pdf", f.Content)).ToList());

    private Task<MailboxRunResult> Poll() => As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().ProcessMailboxAsync(_entity));

    private Task<List<IntakeMessage>> Messages() => As(_tenantA, _staff, async s => await s.GetRequiredService<InvoicesDbContext>().IntakeMessages.AsNoTracking().ToListAsync());

    private Task<List<Invoice>> InvoicesOf(Guid tenant) => As(tenant, _staff, async s => await s.GetRequiredService<InvoicesDbContext>().Invoices.AsNoTracking().ToListAsync());

    // --- document channel and extraction --------------------------------------------------------------------

    [Fact]
    public async Task A_document_upload_creates_an_incomplete_draft_that_cannot_be_submitted_until_completed()
    {
        var draft = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().CreateFromDocumentAsync(_entity, _vendor, "scan.pdf", new MemoryStream(Pdf)));
        Assert.Equal(InvoiceSource.Document, draft.Source);
        Assert.Equal(InvoiceStatus.Draft, draft.Status);
        Assert.Equal("", draft.InvoiceNumber);
        Assert.Equal("SAR", draft.CurrencyCode);
        Assert.Equal(AttachmentRole.Original, Assert.Single(draft.Attachments).Role);

        var incomplete = await Assert.ThrowsAsync<ValidationFailedException>(() => As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(draft.Id)));
        Assert.Equal("incomplete_invoice", incomplete.Code);

        await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(draft.Id,
            new InvoiceData("DOC-1", new DateOnly(2026, 10, 1), null, "SAR", 100, 15, 115, null, null, [])));
        var submitted = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(draft.Id));
        Assert.Equal(InvoiceStatus.Received, submitted.Status);
    }

    [Fact]
    public async Task A_rejected_file_leaves_no_usable_draft()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>()
            .CreateFromDocumentAsync(_entity, _vendor, "fake.pdf", new MemoryStream(Encoding.ASCII.GetBytes("MZ not a pdf")))));
        var all = await InvoicesOf(_tenantA);
        Assert.DoesNotContain(all, i => i.Status == InvoiceStatus.Draft);
        Assert.Contains(all, i => i.Status == InvoiceStatus.Cancelled);
    }

    [Fact]
    public async Task Extraction_runs_on_clean_files_stores_suggestions_and_never_changes_the_invoice()
    {
        var draft = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().CreateFromDocumentAsync(_entity, _vendor, "scan.pdf", new MemoryStream(Pdf)));
        Assert.Equal(0, await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceExtractionService>().RunPendingAsync())); // still in quarantine

        await ActiveVendorKit.ScanAllAsync(_host.Services);
        Assert.Equal(1, await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceExtractionService>().RunPendingAsync()));
        Assert.Equal(0, await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceExtractionService>().RunPendingAsync())); // once per file

        var suggestions = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceExtractionService>().ListAsync(draft.Id));
        var one = Assert.Single(suggestions);
        Assert.Equal("test-ocr", one.Engine);
        Assert.Equal("INV-OCR-9", one.Result.Fields["invoiceNumber"].Value);
        Assert.Equal(0.62, one.Result.Fields["invoiceNumber"].Confidence);

        var after = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceService>().GetAsync(draft.Id));
        Assert.Equal("", after.InvoiceNumber); // a suggestion is never applied by the system
        Assert.Equal(0m, after.Total);
        Assert.Equal(1, after.Version);

        await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceExtractionService>().DismissAsync(one.Id).ContinueWith(_ => 0));
        Assert.Equal(ExtractionStatus.Dismissed, Assert.Single(await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceExtractionService>().ListAsync(draft.Id))).Status);
    }

    [Fact]
    public async Task Infected_files_are_never_extracted()
    {
        var draft = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>()
            .CreateFromDocumentAsync(_entity, _vendor, "bad.pdf", new MemoryStream([.. Pdf, .. Encoding.ASCII.GetBytes(Eicar)])));
        await ActiveVendorKit.ScanAllAsync(_host.Services);
        Assert.Equal(0, await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceExtractionService>().RunPendingAsync()));
        Assert.Empty(await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceExtractionService>().ListAsync(draft.Id)));
        Assert.Equal(0, _extractor.Calls);
    }

    // --- email ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mail_from_a_known_vendor_becomes_a_draft_with_its_files()
    {
        _mail.Add(Inbox, Mail("m1", "AP@AlAmal.example", ("invoice.pdf", Pdf), ("terms.pdf", Pdf)));
        var run = await Poll();
        Assert.Equal(new MailboxRunResult(1, 1, 0, 0, 0), run);

        var invoice = Assert.Single(await InvoicesOf(_tenantA));
        Assert.Equal(InvoiceSource.Email, invoice.Source);
        Assert.Equal(_vendor, invoice.VendorId);
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
        Assert.Equal(SystemActors.Intake, invoice.CreatedBy);
        Assert.Equal("System", invoice.CreatedByKind);

        var detail = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceService>().GetAsync(invoice.Id));
        Assert.Equal([AttachmentRole.Original, AttachmentRole.Supporting], detail.Attachments.Select(a => a.Role));

        var message = Assert.Single(await Messages());
        Assert.Equal(IntakeStatus.Processed, message.Status);
        Assert.Equal(invoice.Id, message.InvoiceId);
        Assert.Equal(1, _mail.ProcessedCount(Inbox));
    }

    [Fact]
    public async Task A_replayed_message_creates_no_second_invoice()
    {
        _mail.Add(Inbox, Mail("replay-1", "ap@alamal.example", ("invoice.pdf", Pdf)));
        await Poll();
        _mail.Add(Inbox, Mail("replay-1", "ap@alamal.example", ("invoice.pdf", Pdf))); // the mailbox hands it out again
        var second = await Poll();

        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Created);
        Assert.Single(await InvoicesOf(_tenantA));
        Assert.Single(await Messages());
    }

    [Fact]
    public async Task Mail_from_unknown_or_ambiguous_senders_waits_for_a_person()
    {
        await SetContact(_vendor2, "shared@twins.example");
        var third = await ActiveVendorKit.ActiveVendorAsync(_host.Services, _tenantA, _entity, "Twin Supplier", _maker, _checker);
        await SetContact(third, "shared@twins.example");

        _mail.Add(Inbox, Mail("u1", "stranger@nowhere.example", ("invoice.pdf", Pdf)));
        _mail.Add(Inbox, Mail("u2", "shared@twins.example", ("invoice.pdf", Pdf)));
        var run = await Poll();
        Assert.Equal(2, run.Unmatched);
        Assert.Empty(await InvoicesOf(_tenantA)); // nothing is guessed, no vendor is created

        var queue = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().ListMessagesAsync(IntakeStatus.Unmatched, null, 0, 50));
        Assert.Equal(2, queue.Count);
        Assert.All(queue, m => Assert.Equal(1, m.AttachmentCount));

        // a person decides
        var u1 = queue.Single(m => m.FromAddress.StartsWith("stranger", StringComparison.Ordinal));
        var assigned = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().AssignAsync(u1.Id, _vendor2));
        Assert.Equal(IntakeStatus.Processed, assigned.Status);
        var invoice = Assert.Single(await InvoicesOf(_tenantA));
        Assert.Equal(_vendor2, invoice.VendorId);
        Assert.Equal(_staff, (await As(_tenantA, _staff, s => s.GetRequiredService<InvoicesDbContext>().Attachments.AsNoTracking().SingleAsync())).AddedBy);

        // the attachment now belongs to the invoice and cannot be moved again
        var info = (await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceService>().GetAsync(invoice.Id))).Attachments.Single();
        await Assert.ThrowsAsync<ConflictException>(() => As(_tenantA, _staff, s => s.GetRequiredService<IAttachmentService>()
            .ReassignFromIntakeAsync(info.AttachmentId, "Invoice", Guid.NewGuid().ToString(), null)));

        // already handled messages cannot be assigned or dismissed again
        await Assert.ThrowsAsync<ConflictException>(() => As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().AssignAsync(u1.Id, _vendor2)));
        var u2 = queue.Single(m => m.Id != u1.Id);
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().DismissAsync(u2.Id, " ")))).Code);
        var dismissed = await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().DismissAsync(u2.Id, "Marketing mail"));
        Assert.Equal(IntakeStatus.Dismissed, dismissed.Status);
    }

    [Fact]
    public async Task A_message_cannot_be_assigned_to_a_vendor_that_may_not_transact_with_the_entity()
    {
        _mail.Add(Inbox, Mail("a1", "stranger@nowhere.example", ("invoice.pdf", Pdf)));
        await Poll();
        var message = Assert.Single(await Messages());
        var unlinked = await ActiveVendorKit.ActiveVendorAsync(_host.Services, _tenantA, _otherEntity, "Other Entity Only", _maker, _checker, "AED");
        Assert.Equal("vendor_cannot_transact", (await Assert.ThrowsAsync<ConflictException>(() =>
            As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().AssignAsync(message.Id, unlinked)))).Code);
    }

    [Fact]
    public async Task Mail_without_a_usable_attachment_is_dismissed_with_a_note()
    {
        _mail.Add(Inbox, new InboundMail("x1", "ap@alamal.example", "Hello", DateTimeOffset.UtcNow, [new InboundAttachment("run.exe", "application/octet-stream", [0x4D, 0x5A, 0, 0])]));
        var run = await Poll();
        Assert.Equal(1, run.Unmatched);
        var message = Assert.Single(await Messages());
        Assert.Equal(IntakeStatus.Dismissed, message.Status);
        Assert.Contains("No usable attachment", message.Note);
        Assert.Empty(await InvoicesOf(_tenantA));
    }

    [Fact]
    public async Task A_poison_message_is_retried_then_dead_lettered_without_blocking_good_mail()
    {
        _mail.Add(Inbox, new InboundMail("poison", "ap@alamal.example", "Broken", DateTimeOffset.UtcNow.AddMinutes(-5), new ExplodingList()));
        _mail.Add(Inbox, Mail("good", "ap@alamal.example", ("invoice.pdf", Pdf)));

        var first = await Poll();
        Assert.Equal(1, first.Failed);
        Assert.Equal(1, first.Created); // the good message was not held up
        for (var i = 1; i < IntakeMessage.MaxAttempts; i++) await Poll();

        var poison = (await Messages()).Single(m => m.ExternalMessageId == "poison");
        Assert.Equal(IntakeStatus.DeadLetter, poison.Status);
        Assert.Equal(IntakeMessage.MaxAttempts, poison.Attempts);
        Assert.NotNull(poison.LastError);
        Assert.Equal(2, _mail.ProcessedCount(Inbox)); // a dead letter is taken out of the mailbox

        var again = await Poll();
        Assert.Equal(new MailboxRunResult(0, 0, 0, 0, 0), again);
        Assert.Single(await InvoicesOf(_tenantA));
    }

    [Fact]
    public async Task A_mailbox_that_cannot_be_read_records_the_problem_and_recovers()
    {
        _mail.FailReads = true;
        Assert.Equal(1, (await Poll()).Failed);
        Assert.NotNull((await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().GetMailboxAsync(_entity)))!.LastError);

        _mail.FailReads = false;
        _mail.Add(Inbox, Mail("later", "ap@alamal.example", ("invoice.pdf", Pdf)));
        Assert.Equal(1, (await Poll()).Created);
        Assert.Null((await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().GetMailboxAsync(_entity)))!.LastError);
    }

    [Fact]
    public async Task Disabled_mailboxes_are_not_read_and_the_worker_scan_lists_only_enabled_ones()
    {
        await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().SaveMailboxAsync(_entity, Inbox, false));
        _mail.Add(Inbox, Mail("d1", "ap@alamal.example", ("invoice.pdf", Pdf)));
        Assert.Equal(new MailboxRunResult(0, 0, 0, 0, 0), await Poll());
        Assert.Empty(await Messages());

        await using var scope = _host.Scope(null);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IIntakeScan>().EnabledMailboxesAsync());
        await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().SaveMailboxAsync(_entity, Inbox, true));
        await using var again = _host.Scope(null);
        Assert.Equal([(_tenantA, _entity)], await again.ServiceProvider.GetRequiredService<IIntakeScan>().EnabledMailboxesAsync());
    }

    [Fact]
    public async Task Mailbox_settings_are_validated_and_unique()
    {
        Assert.Equal("invalid_email", (await Assert.ThrowsAsync<ValidationFailedException>(() => As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().SaveMailboxAsync(_otherEntity, "not-an-address", true)))).Code);
        Assert.Equal("mailbox_in_use", (await Assert.ThrowsAsync<ConflictException>(() => As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().SaveMailboxAsync(_otherEntity, Inbox.ToUpperInvariant(), true)))).Code);
        Assert.Equal("unknown_legal_entity", (await Assert.ThrowsAsync<ValidationFailedException>(() => As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().SaveMailboxAsync(Guid.NewGuid(), "x@acme.example", true)))).Code);
        Assert.Null(await As(_tenantA, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().GetMailboxAsync(_otherEntity)));
    }

    [Fact]
    public async Task Another_tenant_sees_no_intake_data()
    {
        _mail.Add(Inbox, Mail("t1", "stranger@nowhere.example", ("invoice.pdf", Pdf)));
        await Poll();
        Assert.Empty(await As(_tenantB, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().ListMessagesAsync(null, null, 0, 50)));
        var message = Assert.Single(await Messages());
        await Assert.ThrowsAsync<NotFoundException>(() => As(_tenantB, _staff, s => s.GetRequiredService<IInvoiceIntakeService>().EntityOfMessageAsync(message.Id)));
    }

    // --- vendor portal ---------------------------------------------------------------------------------------

    private async Task<T> AsVendor<T>(Guid vendorId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _host.Scope(_tenantA, Guid.NewGuid());
        scope.ServiceProvider.GetRequiredService<VendorContext>().SetVendor(vendorId);
        return await action(scope.ServiceProvider);
    }

    private static PortalSubmitInvoice Submission(Guid entity, string number = "P-001", decimal total = 115m) =>
        new(entity, new InvoiceData(number, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "SAR", 100m, 15m, total, "PO-9", null, []));

    [Fact]
    public async Task A_vendor_submits_an_invoice_with_its_file_and_sees_only_its_own()
    {
        var submitted = await AsVendor(_vendor, s => s.GetRequiredService<IPortalInvoiceService>().SubmitAsync(Submission(_entity), "inv.pdf", new MemoryStream(Pdf)));
        Assert.Equal(PortalInvoiceStatus.Received, submitted.Status);
        Assert.Equal("KSA", submitted.EntityName);

        var row = Assert.Single(await InvoicesOf(_tenantA));
        Assert.Equal(InvoiceSource.Portal, row.Source);
        Assert.Equal(InvoiceStatus.Received, row.Status);
        Assert.Equal("Vendor", row.CreatedByKind);

        await AsVendor(_vendor2, s => s.GetRequiredService<IPortalInvoiceService>().SubmitAsync(Submission(_entity, "P-002"), "inv.pdf", new MemoryStream(Pdf)));
        var mine = await AsVendor(_vendor, s => s.GetRequiredService<IPortalInvoiceService>().ListAsync());
        Assert.Equal(["P-001"], mine.Select(i => i.InvoiceNumber));
        await Assert.ThrowsAsync<NotFoundException>(() => AsVendor(_vendor2, s => s.GetRequiredService<IPortalInvoiceService>().GetAsync(submitted.Id)));
    }

    [Fact]
    public async Task A_vendor_cannot_submit_for_an_entity_it_is_not_linked_to_or_with_missing_essentials()
    {
        Assert.Equal("vendor_cannot_transact", (await Assert.ThrowsAsync<ConflictException>(() =>
            AsVendor(_vendor, s => s.GetRequiredService<IPortalInvoiceService>().SubmitAsync(Submission(_otherEntity), "inv.pdf", new MemoryStream(Pdf))))).Code);

        Assert.Equal("incomplete_invoice", (await Assert.ThrowsAsync<ValidationFailedException>(() =>
            AsVendor(_vendor, s => s.GetRequiredService<IPortalInvoiceService>().SubmitAsync(Submission(_entity, total: 0), "inv.pdf", new MemoryStream(Pdf))))).Code);
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            AsVendor(_vendor, s => s.GetRequiredService<IPortalInvoiceService>().SubmitAsync(Submission(_entity, "P-3"), "fake.pdf", new MemoryStream(Encoding.ASCII.GetBytes("MZ no")))));

        // failed submissions leave nothing the vendor or staff would mistake for a live invoice
        Assert.DoesNotContain(await InvoicesOf(_tenantA), i => i.Status is InvoiceStatus.Draft or InvoiceStatus.Received);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, PortalInvoiceStatus.Received)]
    [InlineData(InvoiceStatus.Received, PortalInvoiceStatus.Received)]
    [InlineData(InvoiceStatus.InVerification, PortalInvoiceStatus.InReview)]
    [InlineData(InvoiceStatus.Verified, PortalInvoiceStatus.InReview)]
    [InlineData(InvoiceStatus.ReadyForApproval, PortalInvoiceStatus.InReview)]
    [InlineData(InvoiceStatus.Exception, PortalInvoiceStatus.NeedsAttention)]
    [InlineData(InvoiceStatus.Rejected, PortalInvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Cancelled, PortalInvoiceStatus.Cancelled)]
    public void Vendors_see_a_simplified_status(string internalStatus, string shown) => Assert.Equal(shown, PortalInvoiceStatus.From(internalStatus));

    // --- test doubles ---------------------------------------------------------------------------------------

    private sealed class SuggestingExtractor : IDocumentExtractor
    {
        public int Calls;
        public string Engine => "test-ocr";

        public Task<ExtractionResult> ExtractAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ExtractionResult(Engine, new Dictionary<string, SuggestedField> { ["invoiceNumber"] = new("INV-OCR-9", 0.62), ["total"] = new("115.00", 0.9) },
                [new SuggestedLine("Consulting", "1", "100.00", "100.00", 0.7)]));
        }
    }

    private sealed class ExplodingList : IReadOnlyList<InboundAttachment>
    {
        public int Count => 1;
        public InboundAttachment this[int index] => throw new InvalidOperationException("corrupt attachment");
        public IEnumerator<InboundAttachment> GetEnumerator() => throw new InvalidOperationException("corrupt attachment");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
