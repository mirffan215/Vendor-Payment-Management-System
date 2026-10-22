using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Jobs;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Domain;
using Vpms.Notifications.Domain;
using Vpms.Notifications.Infrastructure;
using Vpms.TestSupport;
using Vpms.Worker;
using Vpms.Workflow.Domain;
using Vpms.Workflow.Infrastructure;

namespace Vpms.IntegrationTests;

/// <summary>Retention policy, legal holds, the retention report and the job that disposes of operational data (ADR-0024).</summary>
[Collection(SqlServerCollection.Name)]
public sealed class RetentionTests(SqlServerFixture sql) : PaymentScenarioBase(sql)
{
    private Task<T> Policy<T>(Guid actor, Func<IRetentionPolicyService, Task<T>> action, Guid? tenant = null) =>
        W.AsAsync(tenant ?? W.TenantA, actor, s => action(s.GetRequiredService<IRetentionPolicyService>()));

    private Task<T> Holds<T>(Guid actor, Func<ILegalHoldService, Task<T>> action, Guid? tenant = null) =>
        W.AsAsync(tenant ?? W.TenantA, actor, s => action(s.GetRequiredService<ILegalHoldService>()));

    private static RetentionSettings Months(params (string Class, int Months)[] values) =>
        new(values.ToDictionary(v => v.Class, v => v.Months), new Dictionary<string, IReadOnlyDictionary<string, int>>());

    private async Task ActivateAsync(RetentionSettings settings, Guid? tenant = null, Guid? maker = null, Guid? checker = null)
    {
        var proposed = await Policy(maker ?? A1, s => s.ProposeAsync(settings, "test"), tenant);
        await Policy(checker ?? A2, s => s.ApproveAsync(proposed.Id!.Value, "ok"), tenant);
    }

    private Task SeedNotificationsAsync(Guid tenant, int count, int monthsOld, Guid? user = null) =>
        W.AsAsync(tenant, W.Admin, async s =>
        {
            var db = s.GetRequiredService<NotificationsDbContext>();
            var owner = user ?? A1;
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                var created = Clock.GetUtcNow().AddMonths(-monthsOld);
                db.Notifications.Add(new Notification { Id = id, UserId = owner, Kind = "ApprovalAssigned", SubjectLabel = "INV", Path = "/x", DedupeKey = "k" + id.ToString("N"), CreatedAt = created });
                db.Deliveries.Add(new NotificationDelivery { Id = Guid.NewGuid(), NotificationId = id, UserId = owner, Channel = "Email", Status = DeliveryStatus.Sent, NextAttemptAt = created, CreatedAt = created });
            }

            await db.SaveChangesAsync();
            return 0;
        });

    private Task<(int Notes, int Deliveries)> CountsAsync(Guid tenant) =>
        W.AsAsync(tenant, W.Admin, async s =>
        {
            var db = s.GetRequiredService<NotificationsDbContext>();
            return (await db.Notifications.CountAsync(), await db.Deliveries.CountAsync());
        });

    private RetentionJob NewJob(int batch = 500) =>
        new(W.Host.ScopeFactory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Retention:Batch"] = batch.ToString() }).Build(), NullLogger<RetentionJob>.Instance);

    // --- policy ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_policy_the_system_defaults_apply()
    {
        var state = await Policy(A1, s => s.GetAsync());
        Assert.Null(state.Active);
        Assert.Equal(84, state.Defaults[RetentionClasses.FinancialRecords]);
        Assert.Equal(6, state.Defaults[RetentionClasses.Notifications]);
        Assert.Equal([RetentionClasses.Notifications, RetentionClasses.WorkflowNotices], state.DisposedAutomatically);
        var context = await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.CustomerAdmin.Contracts.IRetentionPolicyReader>().ContextAsync());
        Assert.Equal(84, context.MonthsFor(RetentionClasses.FinancialRecords, "SA"));
        Assert.False(context.TenantHeld);
    }

    [Fact]
    public async Task A_policy_change_needs_a_second_person_and_the_maker_is_blocked_and_audited()
    {
        var proposed = await Policy(A1, s => s.ProposeAsync(Months((RetentionClasses.Notifications, 12)), "keep longer"));
        Assert.Equal(RetentionStatus.Pending, proposed.Status);
        Assert.Equal(12, proposed.Settings.MonthsFor(RetentionClasses.Notifications, null));

        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Policy(A1, s => s.ApproveAsync(proposed.Id!.Value, "self")));
        var events = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery { Action = "sod.retention-policy.blocked" }, null));
        Assert.Single(events.Items);

        Assert.Equal(RetentionStatus.Pending, (await Policy(A1, s => s.GetAsync())).Pending!.Status);
        var approved = await Policy(A2, s => s.ApproveAsync(proposed.Id!.Value, "fine"));
        Assert.Equal(RetentionStatus.Active, approved.Status);
        var context = await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.CustomerAdmin.Contracts.IRetentionPolicyReader>().ContextAsync());
        Assert.Equal(12, context.MonthsFor(RetentionClasses.Notifications));
    }

    [Fact]
    public async Task A_new_version_replaces_the_active_one_and_only_one_change_waits_at_a_time()
    {
        await ActivateAsync(Months((RetentionClasses.Notifications, 12)));
        var second = await Policy(A1, s => s.ProposeAsync(Months((RetentionClasses.Notifications, 24)), "again"));
        await Assert.ThrowsAsync<ConflictException>(() => Policy(A1, s => s.ProposeAsync(Months((RetentionClasses.Notifications, 36)), "third")));
        await Policy(A1, s => s.CancelAsync(second.Id!.Value));
        await Assert.ThrowsAsync<ConflictException>(() => Policy(A2, s => s.ApproveAsync(second.Id!.Value, "late")));

        await ActivateAsync(Months((RetentionClasses.Notifications, 18)));
        var state = await Policy(A1, s => s.GetAsync());
        Assert.Equal(3, state.Active!.VersionNo);
        Assert.Equal(18, state.Active.Settings.MonthsFor(RetentionClasses.Notifications, null));
    }

    [Fact]
    public async Task Country_settings_win_for_that_countrys_legal_entities()
    {
        var settings = new RetentionSettings(
            new Dictionary<string, int> { [RetentionClasses.FinancialRecords] = 120, [RetentionClasses.Audit] = 180 },
            new Dictionary<string, IReadOnlyDictionary<string, int>> { ["SA"] = new Dictionary<string, int> { [RetentionClasses.FinancialRecords] = 72 } });
        await ActivateAsync(settings);
        var context = await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.CustomerAdmin.Contracts.IRetentionPolicyReader>().ContextAsync());
        Assert.Equal(72, context.MonthsFor(RetentionClasses.FinancialRecords, "SA"));
        Assert.Equal(72, context.MonthsFor(RetentionClasses.FinancialRecords, "sa"));
        Assert.Equal(120, context.MonthsFor(RetentionClasses.FinancialRecords, "AE"));
        Assert.Equal(120, context.MonthsFor(RetentionClasses.FinancialRecords));
    }

    [Theory]
    [InlineData("Unknown", 12, "retention_class_unknown")]
    [InlineData("Notifications", 0, "retention_months_invalid")]
    [InlineData("Notifications", 5000, "retention_months_invalid")]
    public async Task Invalid_settings_are_refused(string dataClass, int months, string code)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Policy(A1, s => s.ProposeAsync(Months((dataClass, months)), "bad")));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task The_audit_trail_cannot_be_kept_for_less_time_than_the_records_it_describes()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Policy(A1, s => s.ProposeAsync(Months((RetentionClasses.Audit, 24)), "short")));
        Assert.Equal("retention_audit_shorter", ex.Code);
        await Assert.ThrowsAsync<ValidationFailedException>(() => Policy(A1, s => s.ProposeAsync(Months((RetentionClasses.FinancialRecords, 200)), "long records")));
    }

    [Fact]
    public async Task One_tenants_policy_never_applies_to_another()
    {
        await ActivateAsync(Months((RetentionClasses.Notifications, 1)));
        var other = await W.AsAsync(W.TenantB, W.Admin, s => s.GetRequiredService<Vpms.CustomerAdmin.Contracts.IRetentionPolicyReader>().ContextAsync());
        Assert.Equal(6, other.MonthsFor(RetentionClasses.Notifications));
        Assert.Null((await Policy(W.Admin, s => s.GetAsync(), W.TenantB)).Active);
    }

    // --- people ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_former_user_can_be_anonymised_and_the_audit_trail_still_points_at_their_id()
    {
        var leaver = await W.UserAsync(W.TenantA, "leaver@t.com", null);
        Task<T> Users<T>(Guid actor, Func<Vpms.Identity.Application.IUserAdminService, Task<T>> action) => W.AsAsync(W.TenantA, actor, s => action(s.GetRequiredService<Vpms.Identity.Application.IUserAdminService>()));

        await Assert.ThrowsAsync<ConflictException>(() => Users(A1, async s => { await s.AnonymiseAsync(leaver); return 0; })); // still active
        await Users(A1, s => s.SetStatusAsync(leaver, false));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Users(leaver, async s => { await s.AnonymiseAsync(leaver); return 0; }));
        await Users(A1, async s => { await s.AnonymiseAsync(leaver); return 0; });

        var listed = (await Users(A1, s => s.ListAsync("anonymised", 0, 10))).Single(u => u.Id == leaver);
        Assert.Equal("Former user", listed.DisplayName);
        Assert.DoesNotContain("leaver", listed.Email);
        Assert.Empty(await Users(A1, s => s.ListAsync("leaver@t.com", 0, 10)));
        await Assert.ThrowsAsync<ConflictException>(() => Users(A1, async s => { await s.AnonymiseAsync(leaver); return 0; })); // once is enough

        var events = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Action: "user.anonymised"), null));
        var e = Assert.Single(events.Items);
        Assert.Equal(leaver.ToString(), e.EntityId);
        Assert.DoesNotContain("leaver", e.ChangeSummaryJson ?? "");
    }

    // --- legal holds ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hold_is_released_by_a_different_person_with_a_reason()
    {
        var hold = await Holds(A1, s => s.PlaceAsync(HoldScope.Tenant, null, null, "Investigation 2026-44"));
        Assert.Single(await Holds(A1, s => s.ListAsync(true)));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Holds(A1, s => s.ReleaseAsync(hold.Id, "done")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Holds(A2, s => s.ReleaseAsync(hold.Id, " ")));
        var released = await Holds(A2, s => s.ReleaseAsync(hold.Id, "Closed by counsel"));
        Assert.NotNull(released.ReleasedAt);
        Assert.Empty(await Holds(A1, s => s.ListAsync(true)));
        Assert.Single(await Holds(A1, s => s.ListAsync(false)));
        await Assert.ThrowsAsync<ConflictException>(() => Holds(A2, s => s.ReleaseAsync(hold.Id, "again")));
    }

    [Fact]
    public async Task A_hold_needs_a_reason_and_a_scope_that_matches_what_it_names()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => Holds(A1, s => s.PlaceAsync(HoldScope.Tenant, null, null, "")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Holds(A1, s => s.PlaceAsync(HoldScope.Tenant, Entity, null, "x")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Holds(A1, s => s.PlaceAsync(HoldScope.LegalEntity, null, null, "x")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Holds(A1, s => s.PlaceAsync(HoldScope.LegalEntity, Guid.NewGuid(), null, "no such entity")));
        Assert.NotNull(await Holds(A1, s => s.PlaceAsync(HoldScope.LegalEntity, Entity, null, "entity audit")));
        Assert.NotNull(await Holds(A1, s => s.PlaceAsync(HoldScope.Vendor, null, Vendor, "vendor dispute")));
    }

    [Fact]
    public async Task Holds_belong_to_their_tenant()
    {
        await Holds(A1, s => s.PlaceAsync(HoldScope.Tenant, null, null, "A only"));
        Assert.Empty(await Holds(W.Admin, s => s.ListAsync(false), W.TenantB));
    }

    // --- the job ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Old_notifications_and_their_deliveries_are_deleted_and_recent_ones_stay()
    {
        await SeedNotificationsAsync(W.TenantA, 3, monthsOld: 8);
        await SeedNotificationsAsync(W.TenantA, 2, monthsOld: 2);
        Assert.Equal((5, 5), await CountsAsync(W.TenantA));

        var removed = await NewJob().RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, removed);
        Assert.Equal((2, 2), await CountsAsync(W.TenantA));
        var events = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery { Action = "retention.disposed" }, null));
        var run = Assert.Single(events.Items);
        Assert.Equal(RetentionClasses.Notifications, run.EntityId);
        Assert.Contains("\"removed\":3", run.ChangeSummaryJson);
    }

    [Fact]
    public async Task Each_tenant_is_cleaned_by_its_own_policy_and_never_by_anothers()
    {
        await SeedNotificationsAsync(W.TenantA, 2, monthsOld: 3);
        await SeedNotificationsAsync(W.TenantB, 2, monthsOld: 3, user: Guid.NewGuid());
        await ActivateAsync(Months((RetentionClasses.Notifications, 1)));          // A keeps one month
        await ActivateAsync(Months((RetentionClasses.Notifications, 24)), W.TenantB, W.Admin, Guid.NewGuid()); // B keeps two years

        await NewJob().RunOnceAsync(CancellationToken.None);

        Assert.Equal((0, 0), await CountsAsync(W.TenantA));
        Assert.Equal((2, 2), await CountsAsync(W.TenantB));
    }

    [Fact]
    public async Task A_tenant_wide_hold_stops_all_disposal_until_it_is_released()
    {
        await SeedNotificationsAsync(W.TenantA, 3, monthsOld: 9);
        var hold = await Holds(A1, s => s.PlaceAsync(HoldScope.Tenant, null, null, "Litigation"));

        Assert.Equal(0, await NewJob().RunOnceAsync(CancellationToken.None));
        Assert.Equal((3, 3), await CountsAsync(W.TenantA));
        Assert.True((await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<IRetentionReportService>().BuildAsync())).All(r => r.OnHold));

        await Holds(A2, s => s.ReleaseAsync(hold.Id, "Closed"));
        Assert.Equal(3, await NewJob().RunOnceAsync(CancellationToken.None));
        Assert.Equal((0, 0), await CountsAsync(W.TenantA));
    }

    [Fact]
    public async Task Large_backlogs_are_cleared_in_batches_in_one_run()
    {
        await SeedNotificationsAsync(W.TenantA, 25, monthsOld: 8);
        Assert.Equal(25, await NewJob(batch: 10).RunOnceAsync(CancellationToken.None));
        Assert.Equal((0, 0), await CountsAsync(W.TenantA));
    }

    [Fact]
    public async Task Old_workflow_notices_go_too_but_the_approval_decisions_that_caused_them_stay()
    {
        var invoice = await VerifiedInvoice(Clerk, "INV-RET");
        await Send(Clerk, invoice.Id);
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            var db = s.GetRequiredService<WorkflowDbContext>();
            var run = await db.Runs.FirstAsync();
            var task = await db.Tasks.FirstAsync();
            db.Notices.Add(new WorkflowNotice { Id = Guid.NewGuid(), UserId = A1, Kind = NoticeKind.Reminder, RunId = run.Id, TaskId = task.Id, SubjectLabel = "INV-RET", CreatedAt = Clock.GetUtcNow().AddMonths(-9) });
            db.Notices.Add(new WorkflowNotice { Id = Guid.NewGuid(), UserId = A1, Kind = NoticeKind.Stalled, RunId = run.Id, TaskId = task.Id, SubjectLabel = "INV-NEW", CreatedAt = Clock.GetUtcNow().AddDays(-3) });
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(1, await NewJob().RunOnceAsync(CancellationToken.None));
        var left = await W.AsAsync(W.TenantA, W.Admin, async s => (await s.GetRequiredService<WorkflowDbContext>().Notices.ToListAsync(), await s.GetRequiredService<WorkflowDbContext>().Tasks.CountAsync()));
        Assert.Equal("INV-NEW", Assert.Single(left.Item1).SubjectLabel);
        Assert.True(left.Item2 > 0);
    }

    // --- the report -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_report_counts_what_has_passed_retention_and_says_what_is_deleted_automatically()
    {
        await SeedNotificationsAsync(W.TenantA, 4, monthsOld: 8);
        await ApprovedInvoice("INV-OLD");
        Clock.Advance(TimeSpan.FromDays(31 * 90)); // far enough that an invoice dated today has passed the 84-month default? no: 90 months
        var rows = await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<IRetentionReportService>().BuildAsync());

        Assert.Equal(RetentionClasses.All.Count, rows.Count);
        var notes = rows.Single(r => r.DataClass == RetentionClasses.Notifications);
        Assert.True(notes.DisposedAutomatically);
        Assert.Equal(4, notes.EligibleCount);
        var financial = rows.Single(r => r.DataClass == RetentionClasses.FinancialRecords);
        Assert.False(financial.DisposedAutomatically);
        Assert.Equal(1, financial.EligibleCount);
        Assert.Null(rows.Single(r => r.DataClass == RetentionClasses.Audit).EligibleCount);

        // nothing was deleted by looking
        Assert.Equal((4, 4), await CountsAsync(W.TenantA));
    }

    [Fact]
    public async Task A_hold_on_the_legal_entity_or_the_vendor_keeps_its_invoices_out_of_the_count()
    {
        await ApprovedInvoice("INV-HELD");
        Clock.Advance(TimeSpan.FromDays(31 * 90));
        int Count(IReadOnlyList<RetentionReportRow> rows) => rows.Single(r => r.DataClass == RetentionClasses.FinancialRecords).EligibleCount!.Value;
        Task<IReadOnlyList<RetentionReportRow>> Report() => W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<IRetentionReportService>().BuildAsync());

        Assert.Equal(1, Count(await Report()));
        var entityHold = await Holds(A1, s => s.PlaceAsync(HoldScope.LegalEntity, Entity, null, "entity"));
        Assert.Equal(0, Count(await Report()));
        await Holds(A2, s => s.ReleaseAsync(entityHold.Id, "ok"));
        Assert.Equal(1, Count(await Report()));
        await Holds(A1, s => s.PlaceAsync(HoldScope.Vendor, null, Vendor, "vendor"));
        Assert.Equal(0, Count(await Report()));
    }

    [Fact]
    public async Task Financial_records_are_never_disposed_by_the_system()
    {
        await ApprovedInvoice("INV-KEEP");
        Clock.Advance(TimeSpan.FromDays(31 * 200));
        Assert.Equal(0, await NewJob().RunOnceAsync(CancellationToken.None));
        var target = W.Host.Services.CreateScope().ServiceProvider.GetServices<IRetentionTarget>().Single(t => t.DataClass == RetentionClasses.FinancialRecords);
        Assert.False(target.CanDispose);
        await Assert.ThrowsAsync<NotSupportedException>(() => target.DisposeAsync(new RetentionContext(Clock.GetUtcNow(), (_, _) => 1, false, new HashSet<Guid>(), new HashSet<Guid>()), 10));
        Assert.Equal(1, await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Invoices.Infrastructure.InvoicesDbContext>().Invoices.CountAsync()));
    }
}
