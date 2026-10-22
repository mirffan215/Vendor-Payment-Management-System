using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.ErpIntegration.Infrastructure;
using Vpms.TestSupport;
using Vpms.Vendors.Contracts;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ErpOutboundTests(SqlServerFixture sql) : ErpScenarioBase(sql)
{
    [Fact]
    public async Task An_approved_invoice_and_its_vendor_reach_the_erp_once()
    {
        var invoice = await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();

        var records = await Records();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(SyncStatus.Succeeded, r.Status));

        // the vendor was created first and its account stored on the link
        var vendor = Assert.Single(Erp.Vendors);
        var link = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IVendorErpSource>().GetAsync(Vendor, Entity));
        Assert.Equal(vendor.Account, link!.ErpAccount);

        var doc = Assert.Single(Erp.Invoices);
        Assert.Equal(invoice.InvoiceNumber, doc.InvoiceNumber);
        Assert.Equal(vendor.Account, doc.VendorAccount);
        Assert.Equal(invoice.Total, doc.Total);
        Assert.Equal(records.Single(r => r.Type == SyncType.PostInvoice).IdempotencyKey, doc.DocumentKey);
    }

    [Fact]
    public async Task Running_the_queue_again_creates_nothing_new()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        var calls = Erp.Calls.Count;

        var (enqueued, run) = await CycleAsync();
        Assert.Equal(0, enqueued);
        Assert.Equal(0, run.Processed);
        Assert.Equal(calls, Erp.Calls.Count);
        Assert.Single(Erp.Invoices);
        Assert.Equal(2, (await Records()).Count);
    }

    [Fact]
    public async Task A_message_replayed_after_a_lost_answer_finds_the_existing_document_instead_of_creating_another()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        var first = (await Records(SyncType.PostInvoice)).Single();

        // The first answer "never arrived": the message goes back to Pending and is sent again.
        await Tamper(async db =>
        {
            var r = await db.SyncRecords.SingleAsync(x => x.Id == first.Id);
            r.Status = SyncStatus.Pending;
            r.Attempts = 0;
            r.NextAttemptAt = Clock.GetUtcNow();
            r.ErpReference = null;
        });
        var (_, run) = await CycleAsync();

        Assert.Equal(1, run.Succeeded);
        Assert.Single(Erp.Invoices);
        Assert.Equal(first.ErpReference, (await Records(SyncType.PostInvoice)).Single().ErpReference);
        var attempts = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<ErpDbContext>().SyncAttempts.Where(a => a.RecordId == first.Id).Select(a => a.Outcome).ToListAsync());
        Assert.Contains(nameof(ErpOutcome.AlreadyExists), attempts);
    }

    [Fact]
    public async Task Two_workers_racing_for_the_same_message_send_it_once()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.ErpIntegration.Application.IErpOutbound>().EnqueueAsync());
        await Tamper(async db => { foreach (var r in await db.SyncRecords.Where(x => x.Type == SyncType.UpsertVendor).ToListAsync()) r.Status = SyncStatus.Succeeded; });
        Erp.AddVendor("V-ERP", "Al Amal Trading");
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<IVendorErpSource>().LinkErpAccountAsync(Vendor, Entity, "V-ERP");
            return true;
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.ErpIntegration.Application.IErpOutbound>().ProcessDueAsync(10))));

        Assert.Equal(1, results.Sum(r => r.Succeeded));
        Assert.Single(Erp.Invoices);
        Assert.Equal(1, Erp.CountCalls("CreateVendorInvoice"));
    }

    [Fact]
    public async Task A_temporary_failure_is_retried_with_a_growing_delay_and_then_succeeds()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        Erp.FailTimes("CreateVendorInvoice", 2, ErpResult.Transient("erp_unavailable", "ERP is down"));
        await CycleAsync(); // the vendor goes through first, then the invoice fails for the first time

        var post = (await Records(SyncType.PostInvoice)).Single();
        Assert.Equal(SyncStatus.Pending, post.Status);
        Assert.Equal(1, post.Attempts);
        Assert.Equal("erp_unavailable", post.LastErrorCode);
        Assert.True(post.NextAttemptAt >= Clock.GetUtcNow() + RetryPolicy.Delay(1));

        // too early: nothing is sent
        var callsBefore = Erp.CountCalls("CreateVendorInvoice");
        await CycleAsync();
        Assert.Equal(callsBefore, Erp.CountCalls("CreateVendorInvoice"));

        await SettleAsync(max: 6, step: TimeSpan.FromMinutes(2));
        var done = (await Records(SyncType.PostInvoice)).Single();
        Assert.Equal(SyncStatus.Succeeded, done.Status);
        Assert.Equal(3, done.Attempts);
        Assert.Single(Erp.Invoices);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_ends_in_the_dead_letter_and_an_operator_can_retry_it()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        Erp.FailTimes("CreateVendorInvoice", 100, ErpResult.Transient("erp_unavailable", "ERP is down"));
        await SettleAsync(max: 30, step: TimeSpan.FromHours(2));

        var dead = (await Records(SyncType.PostInvoice)).Single();
        Assert.Equal(SyncStatus.DeadLetter, dead.Status);
        Assert.Equal("max_attempts", dead.LastErrorCode);
        Assert.Equal(RetryPolicy.MaxAttempts, dead.Attempts);

        Erp.Script("CreateVendorInvoice"); // the ERP is back
        await Assert.ThrowsAsync<ValidationFailedException>(() => Sync(ErpOp, s => s.RetryAsync(dead.Id, " ")));
        var retried = await Sync(ErpOp, s => s.RetryAsync(dead.Id, "ERP restored"));
        Assert.Equal(SyncStatus.Pending, retried.Status);

        await SettleAsync();
        var done = (await Records(SyncType.PostInvoice)).Single();
        Assert.Equal(SyncStatus.Succeeded, done.Status);
        Assert.Equal(ErpOp, done.HandledBy);
        Assert.Single(Erp.Invoices);
    }

    [Fact]
    public async Task A_permanent_rejection_goes_straight_to_the_dead_letter_without_retrying()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        Erp.Script("CreateVendorInvoice", ErpResult.Rejected("posting_period_closed", "The period is closed"));
        await SettleAsync();

        var post = (await Records(SyncType.PostInvoice)).Single();
        Assert.Equal(SyncStatus.DeadLetter, post.Status);
        Assert.Equal("posting_period_closed", post.LastErrorCode);
        Assert.Equal(1, post.Attempts);
        Assert.Empty(Erp.Invoices);

        var detail = await Sync(ErpOp, s => s.GetAsync(post.Id));
        Assert.Single(detail.Attempts);
        Assert.Equal("Rejected", detail.Attempts[0].Outcome);
    }

    [Fact]
    public async Task A_dead_letter_can_be_resolved_by_hand_with_a_reason_and_is_audited()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        Erp.Script("CreateVendorInvoice", ErpResult.Rejected("posting_period_closed"));
        await SettleAsync();
        var post = (await Records(SyncType.PostInvoice)).Single();

        await Assert.ThrowsAsync<ValidationFailedException>(() => Sync(ErpOp, s => s.ResolveAsync(post.Id, "", "ERP-9")));
        var resolved = await Sync(ErpOp, s => s.ResolveAsync(post.Id, "Posted manually in the ERP", "ERP-9"));
        Assert.Equal(SyncStatus.Resolved, resolved.Status);
        Assert.Equal("ERP-9", resolved.ErpReference);
        await Assert.ThrowsAsync<ConflictException>(() => Sync(ErpOp, s => s.RetryAsync(post.Id, "again")));

        var actions = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AsNoTracking().Select(e => e.Action).ToListAsync());
        Assert.Contains("erp.sync.enqueued", actions);
        Assert.Contains("erp.sync.dead-lettered", actions);
        Assert.Contains("erp.sync.resolved-manually", actions);
    }

    [Fact]
    public async Task A_message_whose_invoice_changed_after_queuing_is_retired_not_sent()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.ErpIntegration.Application.IErpOutbound>().EnqueueAsync());
        await Tamper(async db => { foreach (var r in await db.SyncRecords.Where(x => x.Type == SyncType.PostInvoice).ToListAsync()) r.PayloadHash = "changed"; });
        await SettleAsync();

        var post = (await Records(SyncType.PostInvoice)).Single();
        Assert.Equal(SyncStatus.Superseded, post.Status);
        Assert.Empty(Erp.Invoices);
        Assert.Equal(0, Erp.CountCalls("CreateVendorInvoice"));
    }

    [Fact]
    public async Task Only_approved_invoices_are_queued()
    {
        await VerifiedInvoice(Clerk, "NOT-APPROVED");
        await ConnectErpAsync();
        var (enqueued, _) = await CycleAsync();
        Assert.Empty(await Records(SyncType.PostInvoice));
        Assert.Equal(1, enqueued); // the vendor only
    }

    [Fact]
    public async Task Nothing_is_sent_without_an_approved_connection_and_a_mapping_for_the_entity()
    {
        await ApprovedInvoice();
        Assert.Equal(0, (await CycleAsync()).Enqueued);

        var c = await Cfg_(ErpCfg, s => s.ProposeConnectionAsync(ErpConfigTests.Connection(), "connect"));
        await Cfg_(ErpApprover, s => s.ApproveAsync(c.Id!.Value, "ok"));
        Assert.Equal(0, (await CycleAsync()).Enqueued); // connection, but no mapping for the entity yet
        Assert.Empty(await Records());
    }

    [Fact]
    public async Task A_missing_secret_postpones_sending_without_using_up_attempts()
    {
        await ApprovedInvoice();
        await ConnectErpAsync(secretRef: "missing-secret");
        await SettleAsync(max: 3);

        var records = await Records();
        Assert.All(records, r =>
        {
            Assert.Equal(SyncStatus.Pending, r.Status);
            Assert.Equal(0, r.Attempts);
            Assert.Equal("secret_unavailable", r.LastErrorCode);
        });
        Assert.Empty(Erp.Calls);
    }

    [Fact]
    public async Task An_invoice_with_coding_the_mapping_cannot_express_is_dead_lettered_with_a_clear_reason()
    {
        await ApprovedInvoice();
        await ConnectErpAsync(ErpConfigTests.Mapping() with { TaxCodeMap = new Dictionary<string, string> { ["Z"] = "VAT0" } });
        await SettleAsync();

        var post = (await Records(SyncType.PostInvoice)).Single();
        Assert.Equal(SyncStatus.DeadLetter, post.Status);
        Assert.Equal("tax_code_unmapped", post.LastErrorCode);
        Assert.Empty(Erp.Invoices);
    }

    [Fact]
    public async Task A_new_version_of_an_invoice_already_in_the_erp_needs_a_person_and_is_not_posted_twice()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        var posted = (await Records(SyncType.PostInvoice)).Single();

        // pretend what is in the ERP belongs to an earlier version of the invoice; the current approval then looks like a new version
        await Tamper(async db =>
        {
            var r = await db.SyncRecords.SingleAsync(x => x.Id == posted.Id);
            r.ObjectVersion = "0";
            r.IdempotencyKey += ":old";
        });
        await SettleAsync();

        var records = await Records(SyncType.PostInvoice);
        var fresh = records.Single(r => r.Id != posted.Id);
        Assert.Equal(SyncStatus.DeadLetter, fresh.Status);
        Assert.Equal("already_posted_other_version", fresh.LastErrorCode);
        Assert.Single(Erp.Invoices);
    }

    [Fact]
    public async Task A_changed_vendor_is_sent_again_and_keeps_its_account()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        var account = Assert.Single(Erp.Vendors).Account;

        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            var db = s.GetRequiredService<Vpms.Vendors.Infrastructure.VendorsDbContext>();
            var link = await db.EntityLinks.SingleAsync(l => l.VendorId == Vendor);
            link.PaymentTermsCode = "NET60";
            await db.SaveChangesAsync();
            return true;
        });
        await SettleAsync();

        var vendorRecords = await Records(SyncType.UpsertVendor);
        Assert.Equal(2, vendorRecords.Count);
        Assert.All(vendorRecords, r => Assert.Equal(SyncStatus.Succeeded, r.Status));
        Assert.Equal(account, Assert.Single(Erp.Vendors).Account);
    }

    [Fact]
    public async Task Another_tenant_neither_queues_nor_sees_these_messages()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();

        var userB = await W.UserAsync(W.TenantB, "opb@t.com", null, "ERP Operator");
        var seen = await W.AsAsync(W.TenantB, userB, s => s.GetRequiredService<Vpms.ErpIntegration.Application.IErpSyncService>().ListAsync(null, null, null, 0, 50));
        Assert.Empty(seen);
        var post = (await Records(SyncType.PostInvoice)).Single();
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantB, userB, s => s.GetRequiredService<Vpms.ErpIntegration.Application.IErpSyncService>().GetAsync(post.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantB, userB, s => s.GetRequiredService<Vpms.ErpIntegration.Application.IErpSyncService>().RetryAsync(post.Id, "x")));
        Assert.Equal(0, await W.AsAsync(W.TenantB, W.Admin, s => s.GetRequiredService<Vpms.ErpIntegration.Application.IErpOutbound>().EnqueueAsync()));
    }

    [Fact]
    public async Task Queue_history_cannot_be_deleted_and_attempts_cannot_be_changed()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => Tamper(async db => db.SyncRecords.RemoveRange(await db.SyncRecords.ToListAsync())));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => Tamper(async db => (await db.SyncAttempts.FirstAsync()).Outcome = "Edited"));
    }
}
