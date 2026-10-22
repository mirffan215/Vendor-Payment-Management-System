using System.Text;
using System.Text.Json;
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
public sealed class TreasuryTests(SqlServerFixture sql) : PaymentScenarioBase(sql)
{
    private Task<T> Treasury<T>(Guid actor, Func<ITreasuryService, Task<T>> action, Guid? tenant = null) =>
        W.AsAsync(tenant ?? W.TenantA, actor, s => action(s.GetRequiredService<ITreasuryService>()));

    private async Task<ProposalDto> ApprovedProposal(int invoices = 1, string prefix = "INV")
    {
        var proposal = await DraftProposal(invoices, prefix);
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        return await Props(PayApprover, s => s.ApproveAsync(proposal.Id, "ok"));
    }

    private async Task GrantRole(Guid user, string roleName) => await W.AsAsync(W.TenantA, W.Admin, async s =>
    {
        var db = s.GetRequiredService<Vpms.Identity.Infrastructure.IdentityDbContext>();
        var role = await db.Roles.FirstAsync(r => r.Name == roleName);
        db.RoleAssignments.Add(new Vpms.Identity.Domain.RoleAssignment { Id = Guid.NewGuid(), UserId = user, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow, AssignedBy = W.Admin });
        await db.SaveChangesAsync();
        return 0;
    });

    private async Task NewBankDetailsAsync()
    {
        var change = await W.AsAsync(W.TenantA, BankMaker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ProposeAsync(Vendor,
            new Vpms.Vendors.Application.ProposeBankDetails("Al Amal Trading", "Other Bank", null, "SA", "SAR", "SA4420000001234567891234", null, "Moved bank")));
        await W.AsAsync(W.TenantA, BankChecker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ApproveAsync(change.Id, "CB-9 called finance", null));
    }

    // --- release ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_approved_proposal_is_released_by_a_third_person_into_a_signed_instruction()
    {
        var proposal = await ApprovedProposal(2);
        var result = await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id));

        Assert.Equal(ProposalStatus.Released, result.Proposal.Status);
        Assert.Equal(Releaser, result.Proposal.ReleasedBy);
        Assert.Equal(InstructionStatus.Prepared, result.Instruction.Status);
        Assert.Equal(2, result.Instruction.ItemCount);
        Assert.Equal(230m, result.Instruction.TotalAmount);
        Assert.Equal(64, result.Instruction.Hash.Length);
        Assert.NotEqual(result.Instruction.Hash, result.Instruction.Signature);
        Assert.All(await Pay(Preparer, s => s.ListAsync(new PaymentRequestFilter(null, null, null), null, 0, 50)), r => Assert.Equal(RequestStatus.Released, r.Status));
        Assert.True((await Treasury(Releaser, s => s.VerifyAsync(result.Instruction.Id))).Valid);

        var authorizations = await Treasury(PayApprover, s => s.AuthorizationsAsync(proposal.Id));
        var a = Assert.Single(authorizations);
        Assert.Equal(ReleaseDecision.Released, a.Decision);
        Assert.Equal(Releaser, a.AuthorizedBy);
        Assert.Equal(result.Instruction.Id, a.InstructionId);

        Assert.Equal(1, await AuditCount("payment.proposal.released"));
        Assert.Equal(1, await AuditCount("payment.instruction.prepared"));
        Assert.Equal(1, await AuditCount("bank-details.revealed-for-payment")); // both invoices share one account: revealed once
    }

    [Fact]
    public async Task The_instruction_holds_the_beneficiary_and_every_invoice_and_is_never_stored_in_clear()
    {
        var proposal = await ApprovedProposal(2, "FILE");
        var instruction = (await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id))).Instruction;

        var csv = Encoding.UTF8.GetString((await Treasury(Releaser, s => s.DownloadAsync(instruction.Id, "csv"))).Content);
        Assert.Contains("SA0380000000608010167519", csv);
        Assert.Contains("FILE-1", csv);
        Assert.Contains("FILE-2", csv);
        Assert.Contains("\"115.00\"", csv);
        using var json = JsonDocument.Parse((await Treasury(Releaser, s => s.DownloadAsync(instruction.Id, "json"))).Content);
        Assert.Equal(2, json.RootElement.GetProperty("payments").GetArrayLength());
        Assert.Equal(proposal.ProposalNo, json.RootElement.GetProperty("proposalNo").GetString());

        var row = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<PaymentsDbContext>().Instructions.AsNoTracking().SingleAsync());
        Assert.DoesNotContain("SA0380000000608010167519", row.CsvCipher);
        Assert.DoesNotContain("SA0380000000608010167519", row.JsonCipher);
    }

    [Fact]
    public async Task Downloading_is_audited_and_records_the_first_hand_off()
    {
        var proposal = await ApprovedProposal();
        var instruction = (await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id))).Instruction;
        Assert.Null((await Treasury(Releaser, s => s.GetInstructionAsync(proposal.Id)))!.HandedOffBy);

        await Treasury(Releaser, s => s.DownloadAsync(instruction.Id, "csv"));
        await Treasury(PayApprover, s => s.DownloadAsync(instruction.Id, "json"));

        var after = (await Treasury(Releaser, s => s.GetInstructionAsync(proposal.Id)))!;
        Assert.Equal(InstructionStatus.HandedOff, after.Status);
        Assert.Equal(Releaser, after.HandedOffBy);
        Assert.Equal(2, await AuditCount("payment.instruction.downloaded"));
        Assert.Equal("format_invalid", (await Assert.ThrowsAsync<ValidationFailedException>(() => Treasury(Releaser, s => s.DownloadAsync(instruction.Id, "xml")))).Code);
    }

    [Fact]
    public async Task A_tampered_instruction_is_detected_and_will_not_be_handed_off()
    {
        var proposal = await ApprovedProposal();
        var instruction = (await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id))).Instruction;
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<PaymentsDbContext>().Instructions.Where(i => i.Id == instruction.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Hash, new string('0', 64)));
            return 0;
        });

        Assert.False((await Treasury(Releaser, s => s.VerifyAsync(instruction.Id))).Valid);
        Assert.Equal("instruction_tampered", (await Assert.ThrowsAsync<ConflictException>(() => Treasury(Releaser, s => s.DownloadAsync(instruction.Id, "csv")))).Code);
        Assert.Equal(1, await AuditCount("payment.instruction.tampered"));
        Assert.Equal(InstructionStatus.Prepared, (await Treasury(Releaser, s => s.GetInstructionAsync(proposal.Id)))!.Status);
    }

    // --- separation of duties --------------------------------------------------------------------------------

    [Fact]
    public async Task Nobody_who_took_part_can_release_and_every_attempt_is_recorded()
    {
        var proposal = await ApprovedProposal();
        var bankMaker = BankMaker;
        var people = new (string Who, Guid User)[] { ("invoice approver", A1), ("invoice creator", Clerk), ("proposal maker", Preparer), ("proposal approver", PayApprover), ("bank maker", bankMaker), ("bank checker", BankChecker) };
        foreach (var (_, user) in people) await GrantRole(user, "Payment Releaser"); // past the role matrix on purpose: the service itself must refuse

        foreach (var (who, user) in people)
        {
            var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Treasury(user, s => s.ReleaseAsync(proposal.Id)));
            Assert.Equal("release-separation", ex.Rule);
            _ = who;
        }

        Assert.Equal(6, await AuditCount("sod.payment.blocked"));
        var attempts = await Treasury(Releaser, s => s.AuthorizationsAsync(proposal.Id));
        Assert.Equal(6, attempts.Count);
        Assert.All(attempts, a => Assert.Equal(ReleaseDecision.Denied, a.Decision));
        Assert.All(attempts, a => Assert.NotEmpty(a.Separation));
        Assert.Equal(ProposalStatus.Approved, (await Props(Preparer, s => s.GetAsync(proposal.Id))).Status);
        Assert.Null(await Treasury(Releaser, s => s.GetInstructionAsync(proposal.Id)));

        // the unrelated releaser still can
        Assert.Equal(ProposalStatus.Released, (await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id))).Proposal.Status);
    }

    [Fact]
    public async Task The_role_matrix_keeps_invoice_approval_approval_of_payments_and_release_apart()
    {
        async Task Assign(Guid user, string role)
        {
            var id = await RoleId(role);
            await W.AsAsync(W.TenantA, W.Admin, async s =>
            {
                await s.GetRequiredService<Vpms.Identity.Application.IRoleAdminService>().AssignAsync(user, id, null);
                return 0;
            });
        }

        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Assign(A1, "Payment Releaser"));          // approves invoices
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Assign(PayApprover, "Payment Releaser")); // approves proposals
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Assign(Preparer, "Payment Approver"));    // prepares proposals
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Assign(Preparer, "Payment Releaser"));
        await Assign(Releaser, "Invoice Clerk"); // a releaser may do unrelated work
    }

    private Task<Guid> RoleId(string name) => W.AsAsync(W.TenantA, W.Admin, async s => (await s.GetRequiredService<Vpms.Identity.Infrastructure.IdentityDbContext>().Roles.FirstAsync(r => r.Name == name)).Id);

    [Fact]
    public async Task Only_an_approved_proposal_can_be_released_and_only_once()
    {
        var draft = await DraftProposal();
        Assert.Equal("invalid_transition", (await Assert.ThrowsAsync<ConflictException>(() => Treasury(Releaser, s => s.ReleaseAsync(draft.Id)))).Code);
        await Props(Preparer, s => s.SubmitAsync(draft.Id));
        Assert.Equal("invalid_transition", (await Assert.ThrowsAsync<ConflictException>(() => Treasury(Releaser, s => s.ReleaseAsync(draft.Id)))).Code);
        await Props(PayApprover, s => s.ApproveAsync(draft.Id, null));
        await Treasury(Releaser, s => s.ReleaseAsync(draft.Id));
        Assert.Equal("invalid_transition", (await Assert.ThrowsAsync<ConflictException>(() => Treasury(Releaser, s => s.ReleaseAsync(draft.Id)))).Code);
    }

    [Fact]
    public async Task Two_releasers_at_the_same_moment_produce_exactly_one_instruction()
    {
        var other = await W.UserAsync(W.TenantA, "rel2@t.com", null, "Payment Releaser");
        var proposal = await ApprovedProposal();

        var outcomes = await Task.WhenAll(TryRelease(Releaser, proposal.Id), TryRelease(other, proposal.Id));
        Assert.Equal(1, outcomes.Count(ok => ok));
        Assert.Equal(1, await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<PaymentsDbContext>().Instructions.CountAsync()));
        Assert.Equal(ProposalStatus.Released, (await Props(Preparer, s => s.GetAsync(proposal.Id))).Status);
    }

    private async Task<bool> TryRelease(Guid user, Guid proposal)
    {
        try
        {
            await Treasury(user, s => s.ReleaseAsync(proposal));
            return true;
        }
        catch (Exception ex) when (ex is ConflictException or DbUpdateException)
        {
            return false;
        }
    }

    // --- re-check at release ---------------------------------------------------------------------------------

    [Fact]
    public async Task Release_re_checks_the_invoice_and_refuses_when_it_no_longer_matches()
    {
        var proposal = await ApprovedProposal();
        var requestId = proposal.Items[0].PaymentRequestId;
        // a change the eager hook never saw
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<PaymentsDbContext>().Requests.Where(r => r.Id == requestId).ExecuteUpdateAsync(u => u.SetProperty(r => r.InvoiceHash, "stale"));
            return 0;
        });

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Treasury(Releaser, s => s.ReleaseAsync(proposal.Id)));
        Assert.Equal("recheck_failed", ex.Code);
        Assert.Equal(ProposalStatus.Invalidated, (await Props(Preparer, s => s.GetAsync(proposal.Id))).Status);
        var attempt = Assert.Single(await Treasury(Releaser, s => s.AuthorizationsAsync(proposal.Id)));
        Assert.Equal(ReleaseDecision.Denied, attempt.Decision);
        Assert.Contains(attempt.Recheck, f => f.Code == "invoice_changed" && f.Severity == RecheckSeverity.Invalidates);
        Assert.Null(await Treasury(Releaser, s => s.GetInstructionAsync(proposal.Id)));
    }

    [Fact]
    public async Task Release_re_checks_the_bank_details_and_refuses_when_the_account_changed()
    {
        var proposal = await ApprovedProposal();
        await NewBankDetailsAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Treasury(Releaser, s => s.ReleaseAsync(proposal.Id)));
        Assert.Equal("recheck_failed", ex.Code);
        Assert.Equal(ProposalStatus.Invalidated, (await Props(Preparer, s => s.GetAsync(proposal.Id))).Status);
        Assert.Contains((await Treasury(Releaser, s => s.AuthorizationsAsync(proposal.Id))).Single().Recheck, f => f.Code == "bank_details_changed");
        Assert.Equal(0, await AuditCount("bank-details.revealed-for-payment")); // nothing was revealed for a payment that did not go ahead
    }

    [Fact]
    public async Task A_pending_bank_change_only_holds_the_release_back_until_it_is_decided()
    {
        var proposal = await ApprovedProposal();
        var change = await W.AsAsync(W.TenantA, BankMaker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ProposeAsync(Vendor,
            new Vpms.Vendors.Application.ProposeBankDetails("Al Amal Trading", "Other Bank", null, "SA", "SAR", "SA4420000001234567891234", null, "Moved bank")));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Treasury(Releaser, s => s.ReleaseAsync(proposal.Id)));
        Assert.Equal("recheck_blocked", ex.Code);
        Assert.Equal(ProposalStatus.Approved, (await Props(Preparer, s => s.GetAsync(proposal.Id))).Status);

        await W.AsAsync(W.TenantA, BankChecker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().RejectAsync(change.Id, "Could not be confirmed"));
        Assert.Equal(ProposalStatus.Released, (await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id))).Proposal.Status);
        Assert.Equal([ReleaseDecision.Denied, ReleaseDecision.Released], (await Treasury(Releaser, s => s.AuthorizationsAsync(proposal.Id))).Select(a => a.Decision));
    }

    [Fact]
    public async Task A_vendor_that_can_no_longer_be_paid_invalidates_the_proposal_at_release()
    {
        var proposal = await ApprovedProposal();
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<Vpms.Vendors.Infrastructure.VendorsDbContext>().EntityLinks.Where(l => l.VendorId == Vendor)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, "Blocked"));
            return 0;
        });
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Treasury(Releaser, s => s.ReleaseAsync(proposal.Id)));
        Assert.Equal("recheck_failed", ex.Code);
        Assert.Contains((await Treasury(Releaser, s => s.AuthorizationsAsync(proposal.Id))).Single().Recheck, f => f.Code == "vendor_cannot_transact");
    }

    // --- audit -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_step_from_request_to_hand_off_is_on_the_audit_trail_in_order_with_who_did_it()
    {
        var invoice = await ApprovedInvoice("AUD-1");
        var request = await RequestFor(invoice);
        var proposal = (await Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, null, null, null, [request.Id])))).Single();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        await Props(PayApprover, s => s.ApproveAsync(proposal.Id, null));
        var instruction = (await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id))).Instruction;
        await Treasury(Releaser, s => s.DownloadAsync(instruction.Id, "csv"));

        var events = await W.AsAsync(W.TenantA, W.Admin, async s =>
            await s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AsNoTracking()
                .Where(e => e.Action.StartsWith("payment.") || e.Action == "bank-details.revealed-for-payment").OrderBy(e => e.Sequence)
                .Select(e => new { e.Action, e.ActorId }).ToListAsync());

        Assert.Equal(
            ["payment.request.created", "payment.proposal.created", "payment.proposal.submitted", "payment.proposal.approved", "bank-details.revealed-for-payment",
             "payment.proposal.released", "payment.instruction.prepared", "payment.instruction.downloaded"],
            events.Select(e => e.Action));
        Assert.Equal([Preparer, Preparer, Preparer, PayApprover, Releaser, Releaser, Releaser, Releaser], events.Select(e => e.ActorId!.Value));
    }

    // --- after release ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_released_invoice_can_no_longer_be_changed_and_its_request_cannot_be_voided()
    {
        var proposal = await ApprovedProposal();
        await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id));
        var invoice = await Invoice(proposal.Items[0].InvoiceId);
        var data = new InvoiceData(invoice.InvoiceNumber, invoice.InvoiceDate, invoice.DueDate, "SAR", 100, 15, 115, null, "late", [new InvoiceLineInput("Work", 1, 100, 100, "S", 0.15m, 15, null, null)]);

        Assert.Equal("payment_released", (await Assert.ThrowsAsync<ConflictException>(() => W.AsAsync(W.TenantA, Clerk, s => s.GetRequiredService<IInvoiceService>().ReviseAsync(invoice.Id, data, "late")))).Code);
        Assert.Equal("invalid_transition", (await Assert.ThrowsAsync<ConflictException>(() => Props(Preparer, s => s.CancelAsync(proposal.Id, "too late")))).Code);
        Assert.Equal(0, await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IPaymentProposalService>().SweepAsync()));
        Assert.Equal(ProposalStatus.Released, (await Props(Preparer, s => s.GetAsync(proposal.Id))).Status);
    }

    [Fact]
    public async Task Instructions_and_authorizations_are_private_to_the_tenant()
    {
        var proposal = await ApprovedProposal();
        var instruction = (await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id))).Instruction;

        await Assert.ThrowsAsync<NotFoundException>(() => Treasury(Releaser, s => s.DownloadAsync(instruction.Id, "csv"), W.TenantB));
        await Assert.ThrowsAsync<NotFoundException>(() => Treasury(Releaser, s => s.VerifyAsync(instruction.Id), W.TenantB));
        await Assert.ThrowsAsync<NotFoundException>(() => Treasury(Releaser, s => s.ReleaseAsync(proposal.Id), W.TenantB));
        Assert.Empty(await Treasury(Releaser, s => s.AuthorizationsAsync(proposal.Id), W.TenantB));
        Assert.Null(await Treasury(Releaser, s => s.GetInstructionAsync(proposal.Id), W.TenantB));
    }

    [Fact]
    public async Task The_release_authorization_log_cannot_be_rewritten_or_deleted_by_the_application_login()
    {
        var proposal = await ApprovedProposal();
        await Treasury(Releaser, s => s.ReleaseAsync(proposal.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<PaymentsDbContext>().Authorizations.ExecuteUpdateAsync(u => u.SetProperty(a => a.Decision, "Denied"));
            return 0;
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<PaymentsDbContext>().Authorizations.ExecuteDeleteAsync();
            return 0;
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<PaymentsDbContext>().Requests.ExecuteDeleteAsync();
            return 0;
        }));
    }
}
