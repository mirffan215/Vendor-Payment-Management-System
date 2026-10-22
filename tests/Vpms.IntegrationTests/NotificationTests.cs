using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.Notifications.Application;
using Vpms.Notifications.Contracts;
using Vpms.Notifications.Domain;
using Vpms.Notifications.Infrastructure;
using Vpms.TestSupport;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class NotificationTests(SqlServerFixture sql) : ErpScenarioBase(sql)
{
    private Task<SweepResult> Sweep(Guid? tenant = null) => W.AsAsync(tenant ?? W.TenantA, W.Admin, s => s.GetRequiredService<INotificationSweep>().RunAsync());

    private Task<T> Notes<T>(Guid user, Func<INotificationService, Task<T>> action, Guid? tenant = null) => W.AsAsync(tenant ?? W.TenantA, user, s => action(s.GetRequiredService<INotificationService>()));

    private Task<IReadOnlyList<NotificationDto>> Inbox(Guid user, string locale = "en") => Notes(user, s => s.ListAsync(false, 0, 50, locale));

    private async Task WithOneStepWorkflow()
    {
        // PaymentScenarioBase already published a one-step workflow for A1.
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_approval_task_tells_its_approver_once_and_nobody_else()
    {
        await WithOneStepWorkflow();
        var invoice = await VerifiedInvoice(Clerk, "INV-1");
        await Send(Clerk, invoice.Id);

        var first = await Sweep();
        Assert.True(first.Created >= 1);
        var mine = await Inbox(A1);
        var note = Assert.Single(mine, n => n.Kind == NotificationKinds.ApprovalAssigned);
        Assert.Contains("INV-1", note.Title);
        Assert.StartsWith("/approvals/", note.Path);
        Assert.True(note.Mandatory);
        Assert.Null(note.ReadAt);
        Assert.Empty(await Inbox(A2));

        var second = await Sweep();
        Assert.Equal(0, second.Created);
        Assert.Single(await Inbox(A1), n => n.Kind == NotificationKinds.ApprovalAssigned);
    }

    [Fact]
    public async Task The_text_is_rendered_in_the_readers_language()
    {
        var invoice = await VerifiedInvoice(Clerk, "INV-2");
        await Send(Clerk, invoice.Id);
        await Sweep();
        var en = (await Inbox(A1, "en")).Single(n => n.Kind == NotificationKinds.ApprovalAssigned);
        var ar = (await Inbox(A1, "ar")).Single(n => n.Kind == NotificationKinds.ApprovalAssigned);
        Assert.StartsWith("Approval needed:", en.Title);
        Assert.StartsWith("مطلوب اعتمادك:", ar.Title);
        Assert.DoesNotContain("{{", en.Body + ar.Body);
        Assert.DoesNotContain("/approvals/", en.Body); // the in-app text does not repeat the link
    }

    [Fact]
    public async Task Reading_marks_only_my_own_and_unread_counts_follow()
    {
        var invoice = await VerifiedInvoice(Clerk, "INV-3");
        await Send(Clerk, invoice.Id);
        await Sweep();
        var note = (await Inbox(A1)).Single(n => n.Kind == NotificationKinds.ApprovalAssigned);

        Assert.Equal(1, await Notes(A1, s => s.UnreadCountAsync()));
        await Assert.ThrowsAsync<NotFoundException>(() => Notes(A2, async s => { await s.MarkReadAsync(note.Id); return 0; }));
        await Notes(A1, async s => { await s.MarkReadAsync(note.Id); return 0; });
        Assert.Equal(0, await Notes(A1, s => s.UnreadCountAsync()));
        Assert.Empty(await Notes<IReadOnlyList<NotificationDto>>(A1, s => s.ListAsync(true, 0, 50, "en")));
        Assert.NotNull((await Inbox(A1)).Single().ReadAt);
        Assert.Equal(0, await Notes(A1, s => s.MarkAllReadAsync()));
    }

    [Fact]
    public async Task Mandatory_notifications_cannot_be_switched_off_and_optional_ones_can()
    {
        await Assert.ThrowsAsync<ConflictException>(() => Notes(A1, s => s.SetPreferenceAsync(NotificationKinds.ApprovalAssigned, NotificationChannel.InApp, false)));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Notes(A1, s => s.SetPreferenceAsync("Nope", NotificationChannel.InApp, false)));
        var prefs = await Notes(A1, s => s.PreferencesAsync());
        Assert.All(prefs.Where(p => p.Kind == NotificationKinds.ApprovalAssigned), p => Assert.True(p.Enabled && p.Mandatory));

        await Notes(PayApprover, s => s.SetPreferenceAsync(NotificationKinds.PaymentAwaitingApproval, NotificationChannel.InApp, false));
        var other = await W.UserAsync(W.TenantA, "payappr2@t.com", null, "Payment Approver");

        var proposal = await DraftProposal();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        await Sweep();
        Assert.DoesNotContain(await Inbox(PayApprover), n => n.Kind == NotificationKinds.PaymentAwaitingApproval); // opted out
        Assert.Contains(await Inbox(other), n => n.Kind == NotificationKinds.PaymentAwaitingApproval);              // another approver is told
        Assert.DoesNotContain(await Inbox(Preparer), n => n.Kind == NotificationKinds.PaymentAwaitingApproval);     // not the preparer
    }

    [Fact]
    public async Task A_dead_lettered_erp_message_tells_the_entitys_erp_operators()
    {
        await ApprovedInvoice("INV-9");
        await ConnectErpAsync();
        Erp.Script("CreateVendorInvoice", Vpms.ErpIntegration.Contracts.ErpResult.Rejected("posting_period_closed"));
        await SettleAsync();
        await Sweep();

        var told = (await Inbox(ErpOp)).Single(n => n.Kind == NotificationKinds.ErpDeadLetter);
        Assert.Equal("/erp/sync", told.Path);
        Assert.DoesNotContain(await Inbox(Clerk), n => n.Kind == NotificationKinds.ErpDeadLetter);
        await Sweep();
        Assert.Single(await Inbox(ErpOp), n => n.Kind == NotificationKinds.ErpDeadLetter);
    }

    [Fact]
    public async Task Templates_can_be_reworded_per_tenant_but_only_with_known_placeholders()
    {
        var invoice = await VerifiedInvoice(Clerk, "INV-4");
        await Send(Clerk, invoice.Id);
        await Sweep();
        var admin = await W.UserAsync(W.TenantA, "notifadmin@t.com", null, "Notification Administrator");

        await Assert.ThrowsAsync<ValidationFailedException>(() => W.AsAsync(W.TenantA, admin, s => s.GetRequiredService<INotificationAdmin>().UpsertTemplateAsync(NotificationKinds.ApprovalAssigned, NotificationChannel.InApp, "en", "Hi {{password}}", "x")));
        await W.AsAsync(W.TenantA, admin, s => s.GetRequiredService<INotificationAdmin>().UpsertTemplateAsync(NotificationKinds.ApprovalAssigned, NotificationChannel.InApp, "en", "TODO: {{subject}}", "Please decide ({{detail}})"));

        var changed = (await Inbox(A1, "en")).Single(n => n.Kind == NotificationKinds.ApprovalAssigned);
        Assert.StartsWith("TODO: INV-4", changed.Title);
        Assert.StartsWith("Please decide", changed.Body);
        Assert.StartsWith("مطلوب اعتمادك:", (await Inbox(A1, "ar")).Single(n => n.Kind == NotificationKinds.ApprovalAssigned).Title); // other language untouched

        var listed = await W.AsAsync(W.TenantA, admin, s => s.GetRequiredService<INotificationAdmin>().TemplatesAsync());
        Assert.Contains(listed, t => t.Kind == NotificationKinds.ApprovalAssigned && t.Channel == NotificationChannel.InApp && t.Locale == "en" && t.IsOverride);
        await W.AsAsync(W.TenantA, admin, async s => { await s.GetRequiredService<INotificationAdmin>().ResetTemplateAsync(NotificationKinds.ApprovalAssigned, NotificationChannel.InApp, "en"); return 0; });
        Assert.StartsWith("Approval needed:", (await Inbox(A1, "en")).Single(n => n.Kind == NotificationKinds.ApprovalAssigned).Title);

        var actions = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AsNoTracking().Select(e => e.Action).ToListAsync());
        Assert.Contains("notification.template.changed", actions);
        Assert.Contains("notification.template.reset", actions);
    }

    [Fact]
    public async Task Another_tenant_neither_sees_nor_gets_these_notifications()
    {
        var invoice = await VerifiedInvoice(Clerk, "INV-5");
        await Send(Clerk, invoice.Id);
        await Sweep();
        var userB = await W.UserAsync(W.TenantB, "nb@t.com", null, "Invoice Approver");
        Assert.Empty(await Notes<IReadOnlyList<NotificationDto>>(userB, s => s.ListAsync(false, 0, 50, "en"), W.TenantB));
        Assert.Equal(0, (await Sweep(W.TenantB)).Created);
        Assert.Equal(0, await W.AsAsync(W.TenantB, W.Admin, s => s.GetRequiredService<NotificationsDbContext>().Notifications.CountAsync()));
        // and another tenant's template override never applies here
        await W.AsAsync(W.TenantB, W.Admin, s => s.GetRequiredService<INotificationAdmin>().UpsertTemplateAsync(NotificationKinds.ApprovalAssigned, NotificationChannel.InApp, "en", "B only {{subject}}", "b"));
        Assert.StartsWith("Approval needed:", (await Inbox(A1)).Single(n => n.Kind == NotificationKinds.ApprovalAssigned).Title);
    }
}
