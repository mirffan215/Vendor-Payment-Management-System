using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.TestSupport;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

/// <summary>A tenant with a legal entity, an active vendor, a clerk, approvers with authority, and a controllable clock.</summary>
public abstract class ApprovalScenarioBase(SqlServerFixture sql) : IAsyncLifetime
{
    protected FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    protected WorkflowWorld W { get; private set; } = null!;
    protected Guid Entity, Vendor;
    protected Guid Cfg, Chg, Clerk, A1, A2, A3, Dual, Maker, Checker;

    /// <summary>Lets a test class replace or add services (for example fake senders) before the host is built.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public virtual async Task InitializeAsync()
    {
        W = new WorkflowWorld(new IntegrationHost(await sql.CreateDatabaseAsync(), Clock, ConfigureServices));
        await W.InitializeAsync();
        Entity = await W.EntityAsync(W.TenantA, "A1");
        Cfg = await W.UserAsync(W.TenantA, "cfg@t.com", null, "Workflow Administrator");
        Chg = await W.UserAsync(W.TenantA, "chg@t.com", null, "Workflow Change Approver");
        Clerk = await W.UserAsync(W.TenantA, "clerk@t.com", null, "Invoice Clerk");
        A1 = await W.UserAsync(W.TenantA, "a1@t.com", null, "Invoice Approver");
        A2 = await W.UserAsync(W.TenantA, "a2@t.com", null, "Invoice Approver");
        A3 = await W.UserAsync(W.TenantA, "a3@t.com", null, "Invoice Approver");
        Dual = await W.UserAsync(W.TenantA, "dual@t.com", null, "Invoice Clerk", "Invoice Approver"); // seeded past the role matrix on purpose
        Maker = await W.UserAsync(W.TenantA, "vm@t.com", null);
        Checker = await W.UserAsync(W.TenantA, "vc@t.com", null);
        Vendor = await ActiveVendorKit.ActiveVendorAsync(W.Host.Services, W.TenantA, Entity, "Al Amal Trading", Maker, Checker);
        foreach (var u in new[] { A1, A2, A3, Dual }) await W.AuthorityAsync(W.TenantA, Entity, u, from: Clock.GetUtcNow().AddDays(-1));
    }

    public async Task DisposeAsync() => await W.Host.DisposeAsync();

    protected async Task Publish(WorkflowDefinitionSpec spec)
    {
        var p = await W.Workflows(W.TenantA, Cfg, s => s.ProposeAsync(Entity, new ProposeDefinition(spec, false, "test")));
        await W.Workflows(W.TenantA, Chg, s => s.ApproveAsync(p.Id!.Value, null));
    }

    protected async Task<InvoiceDto> VerifiedInvoice(Guid creator, string number = "INV-1", decimal net = 100m, Guid? vendor = null)
    {
        var tax = Math.Round(net * 0.15m, 2);
        var data = new InvoiceData(number, TestDates.Ago(10), TestDates.Ago(10).AddDays(30), "SAR", net, tax, net + tax, null, null,
            [new InvoiceLineInput("Consulting", 1, net, net, "S", 0.15m, tax, null, null)]);
        var created = await W.AsAsync(W.TenantA, creator, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(Entity, vendor ?? Vendor, data)));
        var submitted = await W.AsAsync(W.TenantA, creator, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id));
        Assert.Equal(InvoiceStatus.Verified, submitted.Status);
        return submitted;
    }

    protected Task<InvoiceDto> Send(Guid actor, Guid invoice) => W.AsAsync(W.TenantA, actor, s => s.GetRequiredService<IInvoiceService>().SendForApprovalAsync(invoice));

    protected Task<InvoiceDto> Invoice(Guid id) => W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IInvoiceService>().GetAsync(id));

    protected Task<IReadOnlyList<InboxItemDto>> Inbox(Guid user) => W.AsAsync(W.TenantA, user, s => s.GetRequiredService<IApprovalService>().InboxAsync(null));

    protected Task<RunDto> Decide(Guid user, Guid task, string decision = DecisionKind.Approve, string? comment = null, Guid? actingFor = null) =>
        W.AsAsync(W.TenantA, user, s => s.GetRequiredService<IApprovalService>().DecideAsync(task, new DecideRequest(decision, comment, actingFor)));

    protected async Task<Guid> TaskOf(Guid user) => (await Inbox(user)).Single().TaskId;

    protected Task<IReadOnlyList<RunDto>> Runs(Guid invoice) => W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IApprovalService>().RunsForSubjectAsync("Invoice", invoice));

    protected Task<DelegationDto> Delegate(Guid delegator, Guid delegateTo, TimeSpan? from = null, TimeSpan? length = null, decimal? cap = null, Guid? actor = null, bool admin = false) =>
        W.AsAsync(W.TenantA, actor ?? delegator, s => s.GetRequiredService<IDelegationService>().CreateAsync(
            new CreateDelegation(Entity, delegator == (actor ?? delegator) ? null : delegator, delegateTo, Clock.GetUtcNow() + (from ?? TimeSpan.Zero), Clock.GetUtcNow() + (from ?? TimeSpan.Zero) + (length ?? TimeSpan.FromDays(7)), cap, "Annual leave"), admin));

    protected Task<TimerRunResult> RunTimers() => W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IWorkflowTimers>().RunDueAsync());

    protected Task<IReadOnlyList<NoticeDto>> Notices(Guid user) => W.AsAsync(W.TenantA, user, s => s.GetRequiredService<IWorkflowTimers>().NoticesAsync(false));

    protected async Task<int> AuditCount(string action) => await W.AsAsync(W.TenantA, W.Admin, async s =>
        await s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.CountAsync(e => e.Action == action));

    protected async Task<Guid> SentInvoiceTask(Guid inboxUser, string number = "INV-1", decimal net = 100m, Guid? creator = null)
    {
        var invoice = await VerifiedInvoice(creator ?? Clerk, number, net);
        await Send(creator ?? Clerk, invoice.Id);
        return await TaskOf(inboxUser);
    }
}
