using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.Invoices.Application;
using Vpms.Invoices.Contracts;
using Vpms.Invoices.Domain;
using Vpms.Payments.Contracts;
using Vpms.Payments.Domain;
using Vpms.Payments.Infrastructure;
using Vpms.TestSupport;
using Vpms.Vendors.Contracts;
using Vpms.Workflow.Contracts;
using Vpms.Workflow.Domain;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

/// <summary>The read contracts the payment module builds on: payable invoices, verified bank details, who approved, and the invoice-change hook.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class PaymentContractsTests(SqlServerFixture sql) : ApprovalScenarioBase(sql)
{
    private async Task<InvoiceDto> ApprovedInvoice(string number = "INV-1", decimal net = 100m, Guid? creator = null)
    {
        await Publish(new WorkflowDefinitionSpec { Name = "One", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(A1)] }] });
        var invoice = await VerifiedInvoice(creator ?? Clerk, number, net);
        await Send(creator ?? Clerk, invoice.Id);
        await Decide(A1, await TaskOf(A1));
        return await Invoice(invoice.Id);
    }

    private Task<T> In<T>(Func<IServiceProvider, Task<T>> action, Guid? actor = null) => W.AsAsync(W.TenantA, actor ?? W.Admin, action);

    [Fact]
    public async Task An_approved_invoice_is_payable_for_its_approved_version_with_a_stable_integrity_hash()
    {
        var approved = await ApprovedInvoice();
        var payable = (await In(s => s.GetRequiredService<IPayableInvoiceLookup>().GetAsync(approved.Id)))!;

        Assert.True(payable.IsPayable);
        Assert.Equal(approved.Total, payable.Total);
        Assert.Equal(1, payable.ApprovedVersion);
        Assert.Contains(Clerk, payable.Preparers);
        Assert.DoesNotContain(A1, payable.Preparers);
        var again = (await In(s => s.GetRequiredService<IPayableInvoiceLookup>().GetAsync(approved.Id)))!;
        Assert.Equal(payable.IntegrityHash, again.IntegrityHash);
        Assert.Single(await In(s => s.GetRequiredService<IPayableInvoiceLookup>().ListPayableAsync(Entity, null, 50)));
    }

    [Fact]
    public async Task Only_approved_invoices_are_listed_and_the_hash_changes_when_the_invoice_does()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "One", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(A1)] }] });
        var verified = await VerifiedInvoice(Clerk, "INV-V");
        Assert.Empty(await In(s => s.GetRequiredService<IPayableInvoiceLookup>().ListPayableAsync(null, null, 50)));
        Assert.False((await In(s => s.GetRequiredService<IPayableInvoiceLookup>().GetAsync(verified.Id)))!.IsPayable);

        await Send(Clerk, verified.Id);
        await Decide(A1, await TaskOf(A1));
        var before = (await In(s => s.GetRequiredService<IPayableInvoiceLookup>().GetAsync(verified.Id)))!;
        Assert.True(before.IsPayable);

        // a small revision (default settings make it material, so use the lookup after approval again)
        var data = new InvoiceData(verified.InvoiceNumber, verified.InvoiceDate, verified.DueDate, "SAR", 200, 30, 230, null, null,
            [new InvoiceLineInput("Consulting", 1, 200, 200, "S", 0.15m, 30, null, null)]);
        await In(s => s.GetRequiredService<IInvoiceService>().ReviseAsync(verified.Id, data, "price correction"), Clerk);
        var after = (await In(s => s.GetRequiredService<IPayableInvoiceLookup>().GetAsync(verified.Id)))!;
        Assert.False(after.IsPayable);
        Assert.NotEqual(before.IntegrityHash, after.IntegrityHash);
        Assert.Empty(await In(s => s.GetRequiredService<IPayableInvoiceLookup>().ListPayableAsync(null, null, 50)));
    }

    [Fact]
    public async Task The_lookup_respects_entity_scope_and_tenant()
    {
        var approved = await ApprovedInvoice();
        Assert.Empty(await In(s => s.GetRequiredService<IPayableInvoiceLookup>().ListPayableAsync(null, new HashSet<Guid> { Guid.NewGuid() }, 50)));
        Assert.Null(await W.AsAsync(W.TenantB, W.Admin, s => s.GetRequiredService<IPayableInvoiceLookup>().GetAsync(approved.Id)));
    }

    [Fact]
    public async Task Everyone_who_approved_is_known_including_those_they_acted_for()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "Two", Start = "a", Steps = [new StepSpec { Id = "a", Approvers = [User(A1)], Next = "b" }, new StepSpec { Id = "b", Approvers = [User(A2)] }] });
        await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.Workflow.Application.IDelegationService>().CreateAsync(
            new Vpms.Workflow.Application.CreateDelegation(Entity, null, A3, Clock.GetUtcNow(), Clock.GetUtcNow().AddDays(3), null, "leave"), false));
        var invoice = await VerifiedInvoice(Clerk);
        await Send(Clerk, invoice.Id);
        await Decide(A3, await TaskOf(A3), actingFor: A1);
        Assert.Empty(await In(s => s.GetRequiredService<IApprovalHistory>().GetApproversAsync("Invoice", invoice.Id))); // not approved yet
        await Decide(A2, await TaskOf(A2));

        var approvers = await In(s => s.GetRequiredService<IApprovalHistory>().GetApproversAsync("Invoice", invoice.Id));
        Assert.Equivalent(new[] { A1, A2, A3 }, approvers);
        Assert.Empty(await W.AsAsync(W.TenantB, W.Admin, s => s.GetRequiredService<IApprovalHistory>().GetApproversAsync("Invoice", invoice.Id)));
    }

    [Fact]
    public async Task Verified_bank_details_name_who_proposed_and_who_checked_them()
    {
        var details = await ActiveVendorKit.VerifiedBankDetailsAsync(W.Host.Services, W.TenantA, Vendor, Maker, Checker, "SAR", "SA0380000000608010167519");
        var verified = (await In(s => s.GetRequiredService<IBankDetailsLookup>().GetVerifiedAsync(Vendor, "SAR")))!;
        Assert.Equal(details, verified.Id);
        Assert.Equal(Maker, verified.MadeBy);
        Assert.Equal(Checker, verified.CheckedBy);
        Assert.Null(await In(s => s.GetRequiredService<IBankDetailsLookup>().GetVerifiedAsync(Vendor, "USD")));
    }

    [Fact]
    public async Task Revealing_bank_details_for_payment_is_audited_and_refused_when_no_longer_verified()
    {
        var details = await ActiveVendorKit.VerifiedBankDetailsAsync(W.Host.Services, W.TenantA, Vendor, Maker, Checker, "SAR", "SA0380000000608010167519");
        var revealed = await In(s => s.GetRequiredService<IBankDetailsLookup>().RevealForPaymentAsync(details), Clerk);
        Assert.Equal("SA0380000000608010167519", revealed.Iban);
        Assert.Equal("Al Amal Trading", revealed.AccountName);
        Assert.Equal(1, await AuditCount("bank-details.revealed-for-payment"));

        // a replacement approved later supersedes these details
        var change = await W.AsAsync(W.TenantA, Maker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ProposeAsync(Vendor,
            new Vpms.Vendors.Application.ProposeBankDetails("Al Amal Trading", "Other Bank", null, "SA", "SAR", "SA4420000001234567891234", null, "New account")));
        await W.AsAsync(W.TenantA, Checker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ApproveAsync(change.Id, "CB-1 called finance", null));
        Assert.Equal("bank_details_not_verified", (await Assert.ThrowsAsync<ConflictException>(() => In(s => s.GetRequiredService<IBankDetailsLookup>().RevealForPaymentAsync(details)))).Code);
        await Assert.ThrowsAsync<NotFoundException>(() => In(s => s.GetRequiredService<IBankDetailsLookup>().RevealForPaymentAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task Another_tenant_cannot_reveal_bank_details()
    {
        var details = await ActiveVendorKit.VerifiedBankDetailsAsync(W.Host.Services, W.TenantA, Vendor, Maker, Checker, "SAR", "SA0380000000608010167519");
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantB, W.Admin, s => s.GetRequiredService<IBankDetailsLookup>().RevealForPaymentAsync(details)));
    }

    [Fact]
    public async Task Revising_an_invoice_invalidates_its_open_payment_request_and_a_released_payment_blocks_revision()
    {
        var approved = await ApprovedInvoice();
        var payable = (await In(s => s.GetRequiredService<IPayableInvoiceLookup>().GetAsync(approved.Id)))!;
        var requestId = Guid.NewGuid();
        await In(async s =>
        {
            var db = s.GetRequiredService<PaymentsDbContext>();
            db.Requests.Add(new PaymentRequest
            {
                Id = requestId, LegalEntityId = Entity, VendorId = Vendor, InvoiceId = approved.Id, InvoiceNumber = approved.InvoiceNumber, InvoiceVersion = 1, Amount = approved.Total,
                CurrencyCode = "SAR", DueDate = TestDates.Today, InvoiceHash = payable.IntegrityHash, BankDetailsId = Guid.NewGuid(), BankFingerprint = "fp", CreatedBy = Clerk, CreatedAt = Clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
            return 0;
        }, Clerk);

        var data = new InvoiceData(approved.InvoiceNumber, approved.InvoiceDate, approved.DueDate, "SAR", 100, 15, 115, null, "changed-ref",
            [new InvoiceLineInput("Consulting", 1, 100, 100, "S", 0.15m, 15, null, null)]);
        await In(s => s.GetRequiredService<IInvoiceService>().ReviseAsync(approved.Id, data, "reference added"), Clerk);

        var request = await In(s => s.GetRequiredService<PaymentsDbContext>().Requests.AsNoTracking().SingleAsync(r => r.Id == requestId));
        Assert.Equal(RequestStatus.Invalidated, request.Status);
        Assert.Contains("revised", request.StatusReason);
        Assert.Equal(1, await AuditCount("payment.request.invalidated"));
        Assert.False(await In(s => s.GetRequiredService<IPaymentLifecycle>().HasReleasedPaymentAsync(approved.Id)));
    }

    [Fact]
    public async Task A_released_payment_makes_the_invoice_unchangeable()
    {
        var approved = await ApprovedInvoice();
        await In(async s =>
        {
            var db = s.GetRequiredService<PaymentsDbContext>();
            db.Requests.Add(new PaymentRequest
            {
                Id = Guid.NewGuid(), LegalEntityId = Entity, VendorId = Vendor, InvoiceId = approved.Id, InvoiceNumber = approved.InvoiceNumber, InvoiceVersion = 1, Amount = approved.Total,
                CurrencyCode = "SAR", DueDate = TestDates.Today, InvoiceHash = "h", BankDetailsId = Guid.NewGuid(), BankFingerprint = "fp", CreatedBy = Clerk, CreatedAt = Clock.GetUtcNow(),
                Status = RequestStatus.Released,
            });
            await db.SaveChangesAsync();
            return 0;
        }, Clerk);

        Assert.True(await In(s => s.GetRequiredService<IPaymentLifecycle>().HasReleasedPaymentAsync(approved.Id)));
        var data = new InvoiceData(approved.InvoiceNumber, approved.InvoiceDate, approved.DueDate, "SAR", 100, 15, 115, null, "x",
            [new InvoiceLineInput("Consulting", 1, 100, 100, "S", 0.15m, 15, null, null)]);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => In(s => s.GetRequiredService<IInvoiceService>().ReviseAsync(approved.Id, data, "late change"), Clerk));
        Assert.Equal("payment_released", ex.Code);
    }
}
