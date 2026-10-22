using Microsoft.Extensions.DependencyInjection;
using Vpms.Invoices.Application;
using Vpms.Payments.Application;
using Vpms.TestSupport;
using Vpms.Workflow.Domain;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

/// <summary>Adds treasury people, verified bank details and approved invoices to the approval scenario.</summary>
public abstract class PaymentScenarioBase(SqlServerFixture sql) : ApprovalScenarioBase(sql)
{
    protected Guid Preparer, PayApprover, Releaser, BankMaker, BankChecker, Preparer2;
    protected const string DefaultIban = "SA0380000000608010167519";
    protected Guid BankDetailsId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Preparer = await W.UserAsync(W.TenantA, "prep@t.com", null, "Payment Preparer");
        Preparer2 = await W.UserAsync(W.TenantA, "prep2@t.com", null, "Payment Preparer");
        PayApprover = await W.UserAsync(W.TenantA, "payappr@t.com", null, "Payment Approver");
        Releaser = await W.UserAsync(W.TenantA, "rel@t.com", null, "Payment Releaser");
        BankMaker = await W.UserAsync(W.TenantA, "bm@t.com", null);
        BankChecker = await W.UserAsync(W.TenantA, "bc@t.com", null);
        BankDetailsId = await ActiveVendorKit.VerifiedBankDetailsAsync(W.Host.Services, W.TenantA, Vendor, BankMaker, BankChecker, "SAR", DefaultIban);
        await Publish(new WorkflowDefinitionSpec { Name = "One", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(A1)] }] });
    }

    /// <summary>An invoice approved by A1 (default creator: the clerk).</summary>
    protected async Task<InvoiceDto> ApprovedInvoice(string number = "INV-1", decimal net = 100m, Guid? creator = null, Guid? vendor = null)
    {
        var invoice = await VerifiedInvoice(creator ?? Clerk, number, net, vendor);
        await Send(creator ?? Clerk, invoice.Id);
        await Decide(A1, await TaskOf(A1));
        return await Invoice(invoice.Id);
    }

    protected Task<T> Pay<T>(Guid actor, Func<IPaymentRequestService, Task<T>> action) => W.AsAsync(W.TenantA, actor, s => action(s.GetRequiredService<IPaymentRequestService>()));

    protected Task<T> Props<T>(Guid actor, Func<IPaymentProposalService, Task<T>> action) => W.AsAsync(W.TenantA, actor, s => action(s.GetRequiredService<IPaymentProposalService>()));

    /// <summary>A second active vendor with verified bank details, linked to the entity.</summary>
    protected async Task<Guid> OtherVendor(string name = "Beta Supplies")
    {
        var v = await ActiveVendorKit.ActiveVendorAsync(W.Host.Services, W.TenantA, Entity, name, Maker, Checker);
        await ActiveVendorKit.VerifiedBankDetailsAsync(W.Host.Services, W.TenantA, v, BankMaker, BankChecker, "SAR", "SA4420000001234567891234");
        return v;
    }

    /// <summary>Approved invoices for the default vendor, each with a request, grouped into one draft proposal made by the preparer.</summary>
    protected async Task<ProposalDto> DraftProposal(int invoices = 1, string prefix = "INV")
    {
        var ids = new List<Guid>();
        for (var i = 1; i <= invoices; i++) ids.Add((await RequestFor(await ApprovedInvoice($"{prefix}-{i}"))).Id);
        return (await Props(Preparer, s => s.CreateAsync(new CreateProposals(Entity, null, null, null, ids)))).Single();
    }

    protected async Task<PaymentRequestDto> RequestFor(InvoiceDto invoice, Guid? by = null)
    {
        var r = (await Pay(by ?? Preparer, s => s.CreateAsync([invoice.Id]))).Single();
        Assert.NotNull(r.Request);
        return r.Request!;
    }
}
