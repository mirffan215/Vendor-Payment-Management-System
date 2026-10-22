using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.ErpIntegration.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ErpReconciliationTests(SqlServerFixture sql) : ErpScenarioBase(sql)
{
    private Task<T> Recon<T>(Guid actor, Func<IReconciliationService, Task<T>> action) => W.AsAsync(W.TenantA, actor, s => action(s.GetRequiredService<IReconciliationService>()));

    private Task<IReadOnlyList<ReconItemDto>> Items(string? status = null) => Recon(ErpOp, s => s.ItemsAsync(Entity, status, null, null, 0, 100));

    private async Task<Dictionary<string, string>> PostFiveInvoices()
    {
        for (var i = 1; i <= 5; i++) await ApprovedInvoice($"INV-{i}", 100m + i);
        await ConnectErpAsync();
        await SettleAsync();
        Assert.Equal(5, Erp.Invoices.Count);
        return Erp.Invoices.ToDictionary(i => i.InvoiceNumber, i => i.DocumentKey!);
    }

    [Fact]
    public async Task When_the_systems_agree_the_report_is_empty()
    {
        await PostFiveInvoices();
        var run = await Recon(ErpOp, s => s.RunAsync(Entity));
        Assert.Equal(ReconRunStatus.Completed, run.Status);
        Assert.Equal(5, run.VpmsInvoices);
        Assert.Equal(5, run.ErpDocuments);
        Assert.Equal(0, run.OpenItems);
        Assert.Empty(await Items());
    }

    [Fact]
    public async Task Every_kind_of_planted_mismatch_is_found_exactly_once()
    {
        var keys = await PostFiveInvoices();
        var account = Erp.Vendors.Single().Account;
        Erp.RemoveInvoice(keys["INV-1"]);                                                  // missing in the ERP
        Erp.ChangeInvoice(keys["INV-2"], total: 999m);                                     // different amount
        Erp.ChangeInvoice(keys["INV-3"], vendorAccount: "SOMEONE-ELSE");                   // different payee
        Erp.ChangeInvoice(keys["INV-4"], state: ErpDocumentState.Cancelled);               // cancelled in the ERP
        Erp.ChangeInvoice(keys["INV-5"], state: ErpDocumentState.Paid, paidAt: Clock.GetUtcNow()); // paid, but no payment released here
        Erp.AddUnknownInvoice(account, "STRAY-1", 40m);                              // entered directly in the ERP

        var run = await Recon(ErpOp, s => s.RunAsync(Entity));
        Assert.Equal(6, run.NewItems);
        Assert.Equal(6, run.OpenItems);

        var items = await Items();
        Assert.Equal(ReconType.All.Order().ToArray(), items.Select(i => i.Type).Order().ToArray());
        Assert.All(items, i => Assert.Equal(ReconItemStatus.Open, i.Status));

        var amount = items.Single(i => i.Type == ReconType.AmountMismatch);
        Assert.Equal("INV-2", amount.InvoiceNumber);
        Assert.Equal("117.30 SAR", amount.VpmsValue); // 102 net + 15% VAT
        Assert.Equal("999.00 SAR", amount.ErpValue);
        Assert.Equal("SOMEONE-ELSE", items.Single(i => i.Type == ReconType.VendorMismatch).ErpValue);
        Assert.Equal("INV-1", items.Single(i => i.Type == ReconType.MissingInErp).InvoiceNumber);
        Assert.Contains("STRAY-1", items.Single(i => i.Type == ReconType.UnknownInErp).ErpValue);
        Assert.Equal("Paid in ERP", items.Single(i => i.Type == ReconType.PaidInErpNotReleased).ErpValue);
        Assert.Equal("Cancelled in ERP", items.Single(i => i.Type == ReconType.StatusMismatch).ErpValue);

        var summary = await Recon(ErpOp, s => s.SummaryAsync(null));
        Assert.Equal(6, summary.Open);
        Assert.All(ReconType.All, t => Assert.Equal(1, summary.OpenByType[t]));
    }

    [Fact]
    public async Task Running_again_does_not_duplicate_items_and_clears_what_has_been_fixed()
    {
        var keys = await PostFiveInvoices();
        Erp.ChangeInvoice(keys["INV-2"], total: 999m);
        Erp.AddUnknownInvoice(Erp.Vendors.Single().Account, "STRAY-1", 40m);
        await Recon(ErpOp, s => s.RunAsync(Entity));

        var again = await Recon(ErpOp, s => s.RunAsync(Entity));
        Assert.Equal(0, again.NewItems);
        Assert.Equal(2, again.OpenItems);
        Assert.Equal(2, (await Items()).Count);

        Erp.ChangeInvoice(keys["INV-2"], total: 117.30m); // corrected in the ERP
        var fixedRun = await Recon(ErpOp, s => s.RunAsync(Entity));
        Assert.Equal(1, fixedRun.ClearedItems);
        Assert.Equal(1, fixedRun.OpenItems);
        Assert.Equal(ReconItemStatus.Cleared, (await Items()).Single(i => i.Type == ReconType.AmountMismatch).Status);
    }

    [Fact]
    public async Task A_difference_that_changes_becomes_a_new_item_and_the_old_one_is_cleared()
    {
        var keys = await PostFiveInvoices();
        Erp.ChangeInvoice(keys["INV-2"], total: 999m);
        await Recon(ErpOp, s => s.RunAsync(Entity));
        Erp.ChangeInvoice(keys["INV-2"], total: 500m);
        var run = await Recon(ErpOp, s => s.RunAsync(Entity));

        Assert.Equal(1, run.NewItems);
        Assert.Equal(1, run.ClearedItems);
        var amounts = (await Items()).Where(i => i.Type == ReconType.AmountMismatch).ToList();
        Assert.Equal(2, amounts.Count);
        Assert.Equal("500.00 SAR", amounts.Single(i => i.Status == ReconItemStatus.Open).ErpValue);
    }

    [Fact]
    public async Task Ending_a_difference_needs_a_reason_and_is_audited()
    {
        var keys = await PostFiveInvoices();
        Erp.RemoveInvoice(keys["INV-1"]);
        await Recon(ErpOp, s => s.RunAsync(Entity));
        var item = (await Items()).Single();

        await Assert.ThrowsAsync<ValidationFailedException>(() => Recon(ErpOp, s => s.ResolveAsync(item.Id, " ")));
        var resolved = await Recon(ErpOp, s => s.ResolveAsync(item.Id, "Re-posted in the ERP by treasury"));
        Assert.Equal(ReconItemStatus.Resolved, resolved.Status);
        Assert.Equal(ErpOp, resolved.DecidedBy);
        await Assert.ThrowsAsync<ConflictException>(() => Recon(ErpOp, s => s.ResolveAsync(item.Id, "again")));

        var actions = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AsNoTracking().Select(e => e.Action).ToListAsync());
        Assert.Contains("erp.recon.run", actions);
        Assert.Contains("erp.recon.item.resolved", actions);
    }

    [Fact]
    public async Task Accepting_an_amount_difference_needs_a_second_person_and_the_attempt_by_the_same_person_is_audited()
    {
        var keys = await PostFiveInvoices();
        Erp.ChangeInvoice(keys["INV-2"], total: 999m);
        await Recon(ErpOp, s => s.RunAsync(Entity));
        var item = (await Items()).Single();

        var proposed = await Recon(ErpOp, s => s.ProposeAcceptanceAsync(item.Id, "Agreed with the supplier"));
        Assert.Equal(ReconItemStatus.PendingAcceptance, proposed.Status);
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Recon(ErpOp, s => s.ApproveAcceptanceAsync(item.Id, null)));
        var blocked = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.CountAsync(e => e.Action == "sod.erp-recon.blocked"));
        Assert.Equal(1, blocked);

        var accepted = await Recon(ErpApprover, s => s.ApproveAcceptanceAsync(item.Id, null));
        Assert.Equal(ReconItemStatus.Accepted, accepted.Status);
        Assert.Equal(ErpApprover, accepted.DecidedBy);

        // an accepted difference is not raised again while it stays the same
        await Recon(ErpOp, s => s.RunAsync(Entity));
        Assert.Equal(ReconItemStatus.Accepted, Assert.Single(await Items()).Status);
        Assert.Equal(0, (await Recon(ErpOp, s => s.SummaryAsync(null))).Open);
    }

    [Fact]
    public async Task A_rejected_acceptance_puts_the_difference_back_to_open()
    {
        var keys = await PostFiveInvoices();
        Erp.ChangeInvoice(keys["INV-3"], vendorAccount: "OTHER");
        await Recon(ErpOp, s => s.RunAsync(Entity));
        var item = (await Items()).Single();
        await Recon(ErpOp, s => s.ProposeAcceptanceAsync(item.Id, "It is fine"));
        var rejected = await Recon(ErpApprover, s => s.RejectAcceptanceAsync(item.Id, "It is not fine"));
        Assert.Equal(ReconItemStatus.Open, rejected.Status);
        Assert.Equal("It is not fine", rejected.DecisionReason);
    }

    [Fact]
    public async Task A_difference_that_is_not_about_money_or_payee_can_be_accepted_by_one_person()
    {
        await PostFiveInvoices();
        Erp.AddUnknownInvoice(Erp.Vendors.Single().Account, "STRAY-1", 40m);
        await Recon(ErpOp, s => s.RunAsync(Entity));
        var item = (await Items()).Single();
        var accepted = await Recon(ErpOp, s => s.ProposeAcceptanceAsync(item.Id, "Entered by treasury for a one-off"));
        Assert.Equal(ReconItemStatus.Accepted, accepted.Status);
    }

    [Fact]
    public async Task A_run_that_cannot_reach_the_erp_fails_cleanly_and_changes_nothing()
    {
        var keys = await PostFiveInvoices();
        Erp.ChangeInvoice(keys["INV-2"], total: 999m);
        await Recon(ErpOp, s => s.RunAsync(Entity));

        Erp.FailTimes("ListInvoices", 1, ErpResult.Transient("erp_unavailable"));
        var failed = await Recon(ErpOp, s => s.RunAsync(Entity));
        Assert.Equal(ReconRunStatus.Failed, failed.Status);
        Assert.Equal("erp_unavailable", failed.ErrorCode);
        var items = await Items();
        Assert.Equal(ReconItemStatus.Open, Assert.Single(items).Status); // not cleared by a run that saw nothing
    }

    [Fact]
    public async Task A_run_without_an_erp_setup_says_so()
    {
        var run = await Recon(ErpOp, s => s.RunAsync(Entity));
        Assert.Equal(ReconRunStatus.Failed, run.Status);
        Assert.Equal("connection_missing", run.ErrorCode);
    }

    [Fact]
    public async Task The_csv_export_lists_the_differences_and_defuses_spreadsheet_formulas()
    {
        await PostFiveInvoices();
        Erp.AddUnknownInvoice(Erp.Vendors.Single().Account, "=HYPERLINK(\"http://evil\")", 40m);
        await Recon(ErpOp, s => s.RunAsync(Entity));

        var csv = await Recon(ErpOp, s => s.ExportCsvAsync(Entity, null, null));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Type,Status,", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"UnknownInErp\"", lines[1]);
        Assert.DoesNotContain(",\"=HYPERLINK", lines[1]);
        Assert.Contains("'=HYPERLINK", lines[1]);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_run_or_decide_these_differences()
    {
        var keys = await PostFiveInvoices();
        Erp.RemoveInvoice(keys["INV-1"]);
        await Recon(ErpOp, s => s.RunAsync(Entity));
        var item = (await Items()).Single();

        var userB = await W.UserAsync(W.TenantB, "reconb@t.com", null, "ERP Operator");
        Assert.Empty(await W.AsAsync(W.TenantB, userB, s => s.GetRequiredService<IReconciliationService>().ItemsAsync(null, null, null, null, 0, 100)));
        Assert.Empty(await W.AsAsync(W.TenantB, userB, s => s.GetRequiredService<IReconciliationService>().RunsAsync(null, null, 20)));
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantB, userB, s => s.GetRequiredService<IReconciliationService>().ResolveAsync(item.Id, "x")));
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantB, userB, s => s.GetRequiredService<IReconciliationService>().RunAsync(Entity)));
    }
}
