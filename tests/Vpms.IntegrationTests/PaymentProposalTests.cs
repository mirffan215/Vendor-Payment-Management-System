using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Invoices.Application;
using Vpms.Payments.Application;
using Vpms.Payments.Domain;
using Vpms.Payments.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PaymentProposalTests(SqlServerFixture sql) : PaymentScenarioBase(sql)
{
    private async Task<string> RequestStatusOf(Guid requestId) =>
        (await Pay(Preparer, s => s.ListAsync(new PaymentRequestFilter(null, null, null), null, 0, 100))).Single(r => r.Id == requestId).Status;

    // --- grouping ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Requests_are_grouped_into_one_draft_proposal_per_vendor_due_date_and_currency()
    {
        var beta = await OtherVendor();
        foreach (var n in new[] { "A-1", "A-2" }) await RequestFor(await ApprovedInvoice(n));
        await RequestFor(await ApprovedInvoice("B-1", vendor: beta));

        var created = await Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, null, null, null, null)));
        Assert.Equal(2, created.Count);
        var amal = created.Single(p => p.VendorId == Vendor);
        var other = created.Single(p => p.VendorId == beta);
        Assert.Equal(2, amal.Items.Count);
        Assert.Equal(230m, amal.TotalAmount);
        Assert.Single(other.Items);
        Assert.All(created, p => Assert.Equal(ProposalStatus.Draft, p.Status));
        Assert.Equal(["PP-" + Clock.GetUtcNow().Year + "-000001", "PP-" + Clock.GetUtcNow().Year + "-000002"], created.Select(p => p.ProposalNo).OrderBy(x => x));
        Assert.All(await Pay(Preparer, s => s.ListAsync(new PaymentRequestFilter(null, null, null), null, 0, 100)), r => Assert.Equal(RequestStatus.InProposal, r.Status));
        Assert.Equal(2, await AuditCount("payment.proposal.created"));
    }

    [Fact]
    public async Task Filters_and_exact_picks_limit_what_goes_into_proposals_and_a_request_is_in_one_proposal_only()
    {
        var a = await RequestFor(await ApprovedInvoice("A-1"));
        var b = await RequestFor(await ApprovedInvoice("A-2"));

        Assert.Empty(await Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, a.DueDate.AddDays(-1), null, null, null))));
        Assert.Empty(await Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, null, "USD", null, null))));

        var first = (await Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, null, null, null, [a.Id])))).Single();
        Assert.Single(first.Items);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, null, null, null, [a.Id, b.Id]))));
        Assert.Equal("request_not_available", ex.Code);
        Assert.Single((await Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, null, null, null, [b.Id])))).Single().Items);
    }

    [Fact]
    public async Task A_request_can_be_taken_out_of_a_draft_and_an_emptied_draft_cancels_itself()
    {
        var proposal = await DraftProposal(2);
        var first = proposal.Items[0].PaymentRequestId;
        var after = await Props(Preparer, s => s.RemoveRequestAsync(proposal.Id, first));
        Assert.Single(after.Items);
        Assert.Equal(115m, after.TotalAmount);
        Assert.Equal(RequestStatus.Created, await RequestStatusOf(first));

        var emptied = await Props(Preparer, s => s.RemoveRequestAsync(proposal.Id, after.Items[0].PaymentRequestId));
        Assert.Equal(ProposalStatus.Cancelled, emptied.Status);

        var again = await DraftProposal(1, "NEXT");
        await Props(Preparer, s => s.SubmitAsync(again.Id));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Props(Preparer, s => s.RemoveRequestAsync(again.Id, again.Items[0].PaymentRequestId)));
        Assert.Equal("invalid_transition", ex.Code);
    }

    // --- lifecycle and separation ---------------------------------------------------------------------------

    [Fact]
    public async Task A_proposal_is_prepared_by_one_person_and_approved_by_another()
    {
        var proposal = await DraftProposal(2);
        var submitted = await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        Assert.Equal(ProposalStatus.PendingApproval, submitted.Status);
        Assert.Equal(Preparer, submitted.SubmittedBy);

        var approved = await Props(PayApprover, s => s.ApproveAsync(proposal.Id, "ok to pay"));
        Assert.Equal(ProposalStatus.Approved, approved.Status);
        Assert.Equal(PayApprover, approved.ApprovedBy);
        Assert.Equal("ok to pay", approved.ApprovalComment);
        Assert.Equal(1, await AuditCount("payment.proposal.submitted"));
        Assert.Equal(1, await AuditCount("payment.proposal.approved"));
    }

    [Fact]
    public async Task The_maker_cannot_approve_their_own_proposal_even_when_a_database_edit_gave_them_both_roles()
    {
        var both = await W.UserAsync(W.TenantA, "both@t.com", null, "Payment Preparer", "Payment Approver"); // seeded past the role matrix on purpose
        var request = await RequestFor(await ApprovedInvoice());     // requested by the ordinary preparer
        var proposal = (await Props(both, s => s.CreateAsync(new CreateProposals(Entity, null, null, null, [request.Id])))).Single();
        await Props(both, s => s.SubmitAsync(proposal.Id));

        var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Props(both, s => s.ApproveAsync(proposal.Id, null)));
        Assert.Equal("maker-checker", ex.Rule);
        Assert.Equal(1, await AuditCount("sod.payment.blocked"));
        Assert.Equal(ProposalStatus.PendingApproval, (await Props(both, s => s.GetAsync(proposal.Id))).Status);
    }

    [Fact]
    public async Task Someone_who_took_part_in_the_invoices_or_the_bank_details_cannot_approve_the_proposal()
    {
        var proposal = await DraftProposal();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        foreach (var involved in new[] { A1, Clerk, BankMaker, BankChecker })
        {
            // grant the payment-approver role straight to the involved person, past the role matrix
            await W.AsAsync(W.TenantA, W.Admin, async s =>
            {
                var db = s.GetRequiredService<Vpms.Identity.Infrastructure.IdentityDbContext>();
                var role = await db.Roles.FirstAsync(r => r.Name == "Payment Approver");
                db.RoleAssignments.Add(new Vpms.Identity.Domain.RoleAssignment { Id = Guid.NewGuid(), UserId = involved, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow, AssignedBy = W.Admin });
                await db.SaveChangesAsync();
                return 0;
            });
            var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Props(involved, s => s.ApproveAsync(proposal.Id, null)));
            Assert.Equal("involved-in-payment", ex.Rule);
        }

        Assert.Equal(4, await AuditCount("sod.payment.blocked"));
        Assert.Equal(ProposalStatus.Approved, (await Props(PayApprover, s => s.ApproveAsync(proposal.Id, null))).Status);
    }

    [Fact]
    public async Task An_approver_can_send_a_proposal_back_with_a_reason_and_the_maker_cannot()
    {
        var proposal = await DraftProposal();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => Props(PayApprover, s => s.SendBackAsync(proposal.Id, " ")))).Code);
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Props(Preparer, s => s.SendBackAsync(proposal.Id, "I want to change it")));

        var back = await Props(PayApprover, s => s.SendBackAsync(proposal.Id, "Wrong vendor group"));
        Assert.Equal(ProposalStatus.Draft, back.Status);
        Assert.Equal("Wrong vendor group", back.StatusReason);
        Assert.Null(back.SubmittedBy);
        Assert.Equal(ProposalStatus.PendingApproval, (await Props(Preparer, s => s.SubmitAsync(proposal.Id))).Status);
    }

    [Fact]
    public async Task Cancelling_returns_the_requests_to_the_pool()
    {
        var proposal = await DraftProposal(2);
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => Props(Preparer, s => s.CancelAsync(proposal.Id, "")))).Code);
        var cancelled = await Props(Preparer, s => s.CancelAsync(proposal.Id, "not this week"));
        Assert.Equal(ProposalStatus.Cancelled, cancelled.Status);
        Assert.All(proposal.Items, i => Assert.Equal(RequestStatus.Created, RequestStatusOf(i.PaymentRequestId).Result));
        Assert.Equal(1, await AuditCount("payment.proposal.cancelled"));
        Assert.Equal(2, (await Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, null, null, null, null)))).Single().Items.Count);
    }

    [Fact]
    public async Task Steps_must_come_in_order()
    {
        var proposal = await DraftProposal();
        Assert.Equal("invalid_transition", (await Assert.ThrowsAsync<ConflictException>(() => Props(PayApprover, s => s.ApproveAsync(proposal.Id, null)))).Code);
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        Assert.Equal("invalid_transition", (await Assert.ThrowsAsync<ConflictException>(() => Props(Preparer, s => s.SubmitAsync(proposal.Id)))).Code);
    }

    // --- re-checks and invalidation ---------------------------------------------------------------------------

    [Fact]
    public async Task Revising_an_invoice_invalidates_its_whole_proposal_at_once_and_frees_the_other_requests()
    {
        var proposal = await DraftProposal(2);
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        var changed = proposal.Items[0];
        var other = proposal.Items[1];

        var invoice = await Invoice(changed.InvoiceId);
        var data = new InvoiceData(invoice.InvoiceNumber, invoice.InvoiceDate, invoice.DueDate, "SAR", 99, 14.85m, 113.85m, null, null, [new InvoiceLineInput("Work", 1, 99, 99, "S", 0.15m, 14.85m, null, null)]);
        await W.AsAsync(W.TenantA, Clerk, s => s.GetRequiredService<IInvoiceService>().ReviseAsync(changed.InvoiceId, data, "price correction"));

        var after = await Props(Preparer, s => s.GetAsync(proposal.Id));
        Assert.Equal(ProposalStatus.Invalidated, after.Status);
        Assert.Contains("revised", after.StatusReason);
        Assert.Equal(RequestStatus.Invalidated, await RequestStatusOf(changed.PaymentRequestId));
        Assert.Equal(RequestStatus.Created, await RequestStatusOf(other.PaymentRequestId));
        Assert.Equal(1, await AuditCount("payment.proposal.invalidated"));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Props(PayApprover, s => s.ApproveAsync(proposal.Id, null)));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public async Task Approval_re_checks_the_bank_details_and_a_changed_account_invalidates_the_proposal()
    {
        var proposal = await DraftProposal();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));

        var change = await W.AsAsync(W.TenantA, BankMaker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ProposeAsync(Vendor,
            new Vpms.Vendors.Application.ProposeBankDetails("Al Amal Trading", "Other Bank", null, "SA", "SAR", "SA4420000001234567891234", null, "Moved bank")));
        await W.AsAsync(W.TenantA, BankChecker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ApproveAsync(change.Id, "CB-7 called finance", null));

        var preview = await Props(PayApprover, s => s.PreviewRecheckAsync(proposal.Id));
        Assert.True(preview.Invalidates);
        Assert.Contains(preview.Findings, f => f.Code == "bank_details_changed");
        Assert.Equal(ProposalStatus.PendingApproval, (await Props(PayApprover, s => s.GetAsync(proposal.Id))).Status); // a preview changes nothing

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Props(PayApprover, s => s.ApproveAsync(proposal.Id, null)));
        Assert.Equal("recheck_failed", ex.Code);
        var after = await Props(PayApprover, s => s.GetAsync(proposal.Id));
        Assert.Equal(ProposalStatus.Invalidated, after.Status);
        Assert.Contains("bank_details_changed", after.StatusReason);
        Assert.Equal(RequestStatus.Invalidated, await RequestStatusOf(proposal.Items[0].PaymentRequestId));
    }

    [Fact]
    public async Task A_bank_change_waiting_for_a_decision_stops_approval_without_invalidating_anything()
    {
        var proposal = await DraftProposal();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        var change = await W.AsAsync(W.TenantA, BankMaker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ProposeAsync(Vendor,
            new Vpms.Vendors.Application.ProposeBankDetails("Al Amal Trading", "Other Bank", null, "SA", "SAR", "SA4420000001234567891234", null, "Moved bank")));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Props(PayApprover, s => s.ApproveAsync(proposal.Id, null)));
        Assert.Equal("recheck_blocked", ex.Code);
        Assert.Equal(ProposalStatus.PendingApproval, (await Props(PayApprover, s => s.GetAsync(proposal.Id))).Status);

        await W.AsAsync(W.TenantA, BankChecker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().RejectAsync(change.Id, "Not confirmed"));
        Assert.Equal(ProposalStatus.Approved, (await Props(PayApprover, s => s.ApproveAsync(proposal.Id, null))).Status);
    }

    [Fact]
    public async Task The_sweep_catches_a_change_the_eager_hook_missed()
    {
        var proposal = await DraftProposal();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        // the bank details are replaced, but nobody touches the proposal
        var change = await W.AsAsync(W.TenantA, BankMaker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ProposeAsync(Vendor,
            new Vpms.Vendors.Application.ProposeBankDetails("Al Amal Trading", "Other Bank", null, "SA", "SAR", "SA4420000001234567891234", null, "Moved bank")));
        await W.AsAsync(W.TenantA, BankChecker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ApproveAsync(change.Id, "CB-8", null));

        Assert.Contains(W.TenantA, await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IPaymentScan>().TenantsWithOpenProposalsAsync()));
        Assert.Equal(1, await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IPaymentProposalService>().SweepAsync()));
        Assert.Equal(ProposalStatus.Invalidated, (await Props(Preparer, s => s.GetAsync(proposal.Id))).Status);
        Assert.Equal(0, await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IPaymentProposalService>().SweepAsync()));
        Assert.DoesNotContain(W.TenantA, await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IPaymentScan>().TenantsWithOpenProposalsAsync()));
    }

    [Fact]
    public async Task Submitting_re_checks_too_so_a_stale_draft_cannot_enter_approval()
    {
        var proposal = await DraftProposal();
        var request = proposal.Items[0].PaymentRequestId;
        // simulate a request whose invoice hash no longer matches
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<PaymentsDbContext>().Requests.Where(r => r.Id == request).ExecuteUpdateAsync(u => u.SetProperty(r => r.InvoiceHash, "stale"));
            return 0;
        });
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Props(Preparer, s => s.SubmitAsync(proposal.Id)));
        Assert.Equal("recheck_failed", ex.Code);
        Assert.Equal(ProposalStatus.Invalidated, (await Props(Preparer, s => s.GetAsync(proposal.Id))).Status);
    }

    // --- isolation -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Proposals_are_private_to_the_tenant_and_the_entity_scope()
    {
        var proposal = await DraftProposal();
        Assert.Empty(await W.AsAsync(W.TenantB, Preparer, s => s.GetRequiredService<IPaymentProposalService>().ListAsync(new ProposalFilter(null, null), null, 0, 50)));
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantB, Preparer, s => s.GetRequiredService<IPaymentProposalService>().GetAsync(proposal.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantB, PayApprover, s => s.GetRequiredService<IPaymentProposalService>().ApproveAsync(proposal.Id, null)));
        Assert.Empty(await Props(Preparer, s => s.ListAsync(new ProposalFilter(null, null), new HashSet<Guid> { Guid.NewGuid() }, 0, 50)));
        Assert.Single(await Props(Preparer, s => s.ListAsync(new ProposalFilter(ProposalStatus.Draft, Entity), null, 0, 50)));
    }
}
