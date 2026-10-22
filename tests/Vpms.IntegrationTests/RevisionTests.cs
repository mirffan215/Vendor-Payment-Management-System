using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.TestSupport;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class RevisionTests(SqlServerFixture sql) : ApprovalScenarioBase(sql)
{
    private Task<ReviseResult> Revise(Guid actor, InvoiceDto from, Func<InvoiceData, InvoiceData> change, string reason = "Supplier sent a corrected copy") =>
        W.AsAsync(W.TenantA, actor, s => s.GetRequiredService<IInvoiceService>().ReviseAsync(from.Id, change(DataOf(from)), reason));

    private static InvoiceData DataOf(InvoiceDto i) => new(i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.CurrencyCode, i.Subtotal, i.TaxTotal, i.Total, i.PoReference, i.SupplierTaxInvoiceRef,
        i.Lines.Select(l => new InvoiceLineInput(l.Description, l.Quantity, l.UnitPrice, l.LineAmount, l.TaxCode, l.TaxRate, l.TaxAmount, l.PoLineRef, l.DimensionsJson)).ToList(), i.SupplierTaxNumber);

    private static InvoiceData WithNet(InvoiceData d, decimal net)
    {
        var tax = Math.Round(net * 0.15m, 2);
        return d with { Subtotal = net, TaxTotal = tax, Total = net + tax, Lines = [new InvoiceLineInput("Consulting", 1, net, net, "S", 0.15m, tax, null, null)] };
    }

    private async Task Policy(decimal percent, decimal absolute, params string[] fields)
    {
        var cfg = new MaterialityConfig(percent, absolute, fields);
        var p = await W.AsAsync(W.TenantA, Cfg, s => s.GetRequiredService<IMaterialityPolicyService>().ProposeAsync(Entity, cfg, "tolerate small changes"));
        await W.AsAsync(W.TenantA, Chg, s => s.GetRequiredService<IMaterialityPolicyService>().ApproveAsync(p.Id!.Value, null));
    }

    private async Task<InvoiceDto> InApproval(string number = "INV-1", decimal net = 1000m, Guid? creator = null)
    {
        await Publish(new WorkflowDefinitionSpec { Name = "Two", Start = "l1", Steps = [Single("l1", A1, "l2"), Single("l2", A2, null)] });
        var invoice = await VerifiedInvoice(creator ?? Clerk, number, net);
        return await Send(creator ?? Clerk, invoice.Id);
    }

    private static StepSpec Single(string id, Guid approver, string? next) => new() { Id = id, Approvers = [User(approver)], Next = next };

    [Fact]
    public async Task By_default_any_change_to_an_invoice_in_approval_restarts_approval()
    {
        var sent = await InApproval();
        await Decide(A1, await TaskOf(A1));                        // level 1 already approved

        var r = await Revise(Clerk, sent, d => WithNet(d, 1000.01m));
        Assert.True(r.Material);
        Assert.Contains("total", r.ChangedFields);
        Assert.Equal(InvoiceStatus.Verified, r.Invoice.Status);     // re-verified at once
        Assert.Equal(2, r.Invoice.Version);
        Assert.Null(r.Invoice.ApprovedVersion);

        var runs = await Runs(sent.Id);
        Assert.Equal([RunStatus.Cancelled], runs.Select(x => x.Status));
        Assert.Empty(await Inbox(A2));                              // the old run's second level is gone
        Assert.Equal(1, await AuditCount("invoice.revised"));
        Assert.Equal(1, await AuditCount("approval.cancelled"));

        // approval starts from the beginning for the new version, with the old run kept as history
        Clock.Advance(TimeSpan.FromMinutes(5));
        await Send(Clerk, sent.Id);
        Assert.Equal([RunStatus.Active, RunStatus.Cancelled], (await Runs(sent.Id)).Select(x => x.Status));
        Assert.Single(await Inbox(A1));
    }

    [Fact]
    public async Task A_change_inside_the_allowance_keeps_the_approval_running_on_the_new_version()
    {
        await Policy(percent: 1m, absolute: 0m, "total", "taxTotal", "currency");
        var sent = await InApproval(net: 1000m);                     // total 1150
        await Decide(A1, await TaskOf(A1));

        var r = await Revise(Clerk, sent, d => WithNet(d, 1005m));   // total 1155.75, +0.5%
        Assert.False(r.Material);
        Assert.Equal(InvoiceStatus.InApproval, r.Invoice.Status);
        Assert.Equal(2, r.Invoice.Version);

        var run = (await Runs(sent.Id)).Single();
        Assert.Equal(RunStatus.Active, run.Status);
        Assert.Equal(2, run.SubjectVersion);
        Assert.Equal(1155.75m, run.Amount);

        var done = await Decide(A2, await TaskOf(A2));
        Assert.Equal(RunStatus.Approved, done.Status);
        var approved = await Invoice(sent.Id);
        Assert.Equal(InvoiceStatus.Approved, approved.Status);
        Assert.Equal(2, approved.ApprovedVersion);
    }

    [Fact]
    public async Task Small_edits_cannot_add_up_because_each_is_compared_with_what_was_approved()
    {
        await Policy(percent: 1m, absolute: 0m, "total", "taxTotal");
        var sent = await InApproval(net: 1000m);                     // baseline total 1150, allowance 11.5

        var first = await Revise(Clerk, sent, d => WithNet(d, 1005m));    // +5.75: fine
        Assert.False(first.Material);
        var second = await Revise(A3, first.Invoice, d => WithNet(d, 1010m)); // +11.5: still within
        Assert.False(second.Material);
        var third = await Revise(Clerk, second.Invoice, d => WithNet(d, 1011m)); // +12.65 from the baseline: over, though only +1.15 from the last edit
        Assert.True(third.Material);
        Assert.Equal(InvoiceStatus.Verified, third.Invoice.Status);
    }

    [Fact]
    public async Task A_field_that_is_not_listed_never_restarts_approval()
    {
        await Policy(percent: 0m, absolute: 0m, "total");
        var sent = await InApproval();
        var r = await Revise(Clerk, sent, d => d with { PoReference = null, SupplierTaxInvoiceRef = "ref-9" });
        Assert.False(r.Material);
        Assert.Equal(InvoiceStatus.InApproval, r.Invoice.Status);
    }

    [Fact]
    public async Task Changing_an_approved_invoice_materially_voids_the_approval()
    {
        await Policy(percent: 0m, absolute: 0m, "total", "taxTotal");
        var sent = await InApproval();
        await Decide(A1, await TaskOf(A1));
        await Decide(A2, await TaskOf(A2));
        var approved = await Invoice(sent.Id);
        Assert.Equal(InvoiceStatus.Approved, approved.Status);

        var r = await Revise(Clerk, approved, d => WithNet(d, 900m));
        Assert.True(r.Material);
        Assert.Equal(InvoiceStatus.Verified, r.Invoice.Status);
        Assert.Null(r.Invoice.ApprovedAt);
        Assert.Null(r.Invoice.ApprovedVersion);
    }

    [Fact]
    public async Task A_small_change_to_an_approved_invoice_keeps_it_approved_for_the_new_version()
    {
        await Policy(percent: 5m, absolute: 0m, "total", "taxTotal");
        var sent = await InApproval();
        await Decide(A1, await TaskOf(A1));
        await Decide(A2, await TaskOf(A2));

        var r = await Revise(Clerk, await Invoice(sent.Id), d => WithNet(d, 1010m));
        Assert.False(r.Material);
        Assert.Equal(InvoiceStatus.Approved, r.Invoice.Status);
        Assert.Equal(2, r.Invoice.ApprovedVersion);
        Assert.Equal(r.Invoice.Version, r.Invoice.ApprovedVersion);
    }

    [Fact]
    public async Task The_person_who_edits_a_running_approval_can_no_longer_decide_it()
    {
        await Policy(percent: 50m, absolute: 0m, "total", "taxTotal");
        var sent = await InApproval();
        // A1 is the first-level approver and edits the invoice (a small change): they become a maker
        var r = await Revise(A1, sent, d => WithNet(d, 1001m));
        Assert.False(r.Material);

        Assert.Empty(await Inbox(A1));
        var run = (await Runs(sent.Id)).Single();
        Assert.Empty(run.Tasks.Single(t => t.StepId == "l1").Assignees);
        Assert.True(run.NeedsAttention);
    }

    [Fact]
    public async Task Only_invoices_in_approval_or_approved_can_be_revised()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "One", Start = "s", Steps = [Single("s", A1, null)] });
        var verified = await VerifiedInvoice(Clerk);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Revise(Clerk, verified, d => d));
        Assert.Equal("invalid_transition", ex.Code);

        var sent = await Send(Clerk, verified.Id);
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => Revise(Clerk, sent, d => d, " "))).Code);
    }

    [Fact]
    public async Task The_outcome_of_a_cancelled_run_is_ignored_and_a_stale_run_is_replaced_when_approval_starts_again()
    {
        var sent = await InApproval();
        var oldTask = await TaskOf(A1);
        await Revise(Clerk, sent, d => WithNet(d, 500m));                  // material: run cancelled

        await Assert.ThrowsAsync<ConflictException>(() => Decide(A1, oldTask));   // run_not_active
        Assert.Equal(InvoiceStatus.Verified, (await Invoice(sent.Id)).Status);
    }

    [Fact]
    public async Task Materiality_settings_follow_the_same_two_person_rule_as_workflows()
    {
        var cfg = new MaterialityConfig(5m, 0m, ["total"]);
        var p = await W.AsAsync(W.TenantA, Cfg, s => s.GetRequiredService<IMaterialityPolicyService>().ProposeAsync(Entity, cfg, "relax"));
        Assert.Equal(DefinitionStatus.Pending, p.Status);

        // until approved, the strict defaults still apply
        var sent = await InApproval();
        Assert.True((await Revise(Clerk, sent, d => WithNet(d, 1001m))).Material);

        var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => W.AsAsync(W.TenantA, Cfg, s => s.GetRequiredService<IMaterialityPolicyService>().ApproveAsync(p.Id!.Value, null)));
        Assert.Equal("maker-checker", ex.Rule);
        Assert.Equal(1, await AuditCount("sod.materiality-change.blocked"));

        await W.AsAsync(W.TenantA, Chg, s => s.GetRequiredService<IMaterialityPolicyService>().ApproveAsync(p.Id!.Value, null));
        var state = await W.AsAsync(W.TenantA, Cfg, s => s.GetRequiredService<IMaterialityPolicyService>().GetAsync(Entity));
        Assert.Equal(1, state.Active.VersionNo);
        Assert.Equal(5m, state.Active.Config.AmountPercent);
    }

    [Fact]
    public async Task Materiality_settings_are_validated_and_isolated()
    {
        Task<MaterialityDto> Propose(MaterialityConfig c, Guid? tenant = null) =>
            W.AsAsync(tenant ?? W.TenantA, Cfg, s => s.GetRequiredService<IMaterialityPolicyService>().ProposeAsync(Entity, c, "x"));

        Assert.Equal("percent_invalid", (await Assert.ThrowsAsync<ValidationFailedException>(() => Propose(new MaterialityConfig(200m, 0m, ["total"])))).Code);
        Assert.Equal("field_unknown", (await Assert.ThrowsAsync<ValidationFailedException>(() => Propose(new MaterialityConfig(0m, 0m, ["nonsense"])))).Code);
        Assert.Equal("no_change", (await Assert.ThrowsAsync<ConflictException>(() => Propose(MaterialityConfig.Defaults()))).Code);
        await Assert.ThrowsAsync<NotFoundException>(() => Propose(new MaterialityConfig(1m, 0m, ["total"]), W.TenantB));

        var state = await W.AsAsync(W.TenantA, Cfg, s => s.GetRequiredService<IMaterialityPolicyService>().GetAsync(Entity));
        Assert.Equal("Default", state.Active.Status);
    }
}
