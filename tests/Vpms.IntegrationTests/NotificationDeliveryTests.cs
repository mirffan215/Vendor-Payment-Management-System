using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Notifications.Application;
using Vpms.Notifications.Contracts;
using Vpms.Notifications.Domain;
using Vpms.Notifications.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class NotificationDeliveryTests(SqlServerFixture sql) : PaymentScenarioBase(sql)
{
    private sealed class FakeEmail : IEmailSender
    {
        public string Provider => "Smtp";

        public List<OutgoingEmail> Sent { get; } = [];

        public Queue<SendResult> Script { get; } = new();

        public Task<SendResult> SendAsync(string settingsJson, string? secret, OutgoingEmail email, CancellationToken ct = default)
        {
            if (Script.Count > 0) return Task.FromResult(Script.Dequeue());
            Sent.Add(email);
            return Task.FromResult(SendResult.Ok());
        }
    }

    private sealed class FakeWebhook : IWebhookSender
    {
        public List<WebhookMessage> Sent { get; } = [];

        public Task<SendResult> SendAsync(string url, WebhookMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(SendResult.Ok());
        }
    }

    private readonly FakeEmail _email = new();
    private readonly FakeWebhook _hook = new();
    private Guid _notifAdmin, _notifApprover;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _notifAdmin = await W.UserAsync(W.TenantA, "nadmin@t.com", null, "Notification Administrator");
        _notifApprover = await W.UserAsync(W.TenantA, "napprover@t.com", null, "Notification Change Approver");
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IEmailSender>(_email);
        services.AddSingleton<IWebhookSender>(_hook);
        services.Configure<NotificationOptions>(o => { o.BaseUrl = "https://app.acme.com"; o.AllowInternalHosts = true; }); // the test mail server is on loopback
    }

    private Task<T> Svc<T>(Guid actor, Func<INotificationChannelService, Task<T>> action) => W.AsAsync(W.TenantA, actor, s => action(s.GetRequiredService<INotificationChannelService>()));

    private static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.Clone();

    private async Task ActivateEmailAsync()
    {
        var c = await Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Email, Json(new { provider = "Smtp", fromAddress = "noreply@acme.com", fromName = "Acme", smtpHost = "smtp.acme.com", smtpPort = 587, smtpStartTls = true }), "Go live"));
        await Svc(_notifApprover, s => s.ApproveAsync(c.Id, "ok"));
    }

    private async Task ActivateWebhookAsync(params string[] kinds)
    {
        var c = await Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Webhook, Json(new { url = "https://acme.webhook.office.com/hook/1", kinds }), "Team channel"));
        await Svc(_notifApprover, s => s.ApproveAsync(c.Id, null));
    }

    private Task<SweepResult> Sweep() => W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<INotificationSweep>().RunAsync());

    private Task<DeliveryRunResult> Deliver() => W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<INotificationDelivery>().ProcessDueAsync(50));

    private Task<List<NotificationDelivery>> Deliveries() => W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<NotificationsDbContext>().Deliveries.AsNoTracking().OrderBy(d => d.CreatedAt).ToListAsync());

    private async Task ApprovalWaiting(string number = "INV-1")
    {
        var invoice = await VerifiedInvoice(Clerk, number);
        await Send(Clerk, invoice.Id);
    }

    // --- configuration --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_channel_is_off_until_a_different_person_approves_it_and_only_a_secret_name_is_stored()
    {
        var proposed = await Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Email, Json(new { provider = "Graph", fromAddress = "noreply@acme.com", graphTenantId = "t", graphClientId = "c", secretRef = "graph-secret", mailbox = "ap@acme.com" }), "Use Graph"));
        Assert.Equal(ChannelConfigStatus.Pending, proposed.Status);
        Assert.Null((await Svc(_notifAdmin, s => s.GetAsync(NotificationChannel.Email))).Active);

        var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Svc(_notifAdmin, s => s.ApproveAsync(proposed.Id, null)));
        Assert.Equal("maker-checker", ex.Rule);
        var blocked = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.CountAsync(e => e.Action == "sod.notification-channel.blocked"));
        Assert.Equal(1, blocked);

        var active = await Svc(_notifApprover, s => s.ApproveAsync(proposed.Id, "ok"));
        Assert.Equal(ChannelConfigStatus.Active, active.Status);
        var raw = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<NotificationsDbContext>().ChannelConfigs.AsNoTracking().Select(c => c.ConfigJson).SingleAsync());
        Assert.Contains("graph-secret", raw);
        Assert.DoesNotContain(TestSecrets.Value, raw);
    }

    [Fact]
    public async Task Unsafe_or_incomplete_settings_are_refused_before_anything_is_stored()
    {
        // The rest of this class lets mail go to loopback servers; here the production rule applies (internal hosts and unlisted webhook hosts refused).
        W.Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value.AllowInternalHosts = false;
        await Assert.ThrowsAsync<ValidationFailedException>(() => Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Webhook, Json(new { url = "https://evil.example.com/x", kinds = new[] { "ErpDeadLetter" } }), "x")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Email, Json(new { provider = "Smtp", fromAddress = "noreply@acme.com", smtpHost = "10.0.0.5", smtpPort = 25 }), "x")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Email, Json(new { provider = "Graph", fromAddress = "noreply@acme.com", secretRef = "not a name!" }), "x")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.InApp, Json(new { }), "x")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Email, Json(new { provider = "Smtp", fromAddress = "noreply@acme.com", smtpHost = "smtp.acme.com", smtpPort = 587 }), " ")));
    }

    [Fact]
    public async Task Only_one_change_can_wait_and_the_same_settings_are_refused()
    {
        await ActivateEmailAsync();
        var same = await Assert.ThrowsAsync<ConflictException>(() => Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Email, Json(new { provider = "Smtp", fromAddress = "noreply@acme.com", fromName = "Acme", smtpHost = "smtp.acme.com", smtpPort = 587, smtpStartTls = true }), "again")));
        Assert.Equal("no_change", same.Code);
        await Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Email, Json(new { provider = "Smtp", fromAddress = "other@acme.com", smtpHost = "smtp.acme.com", smtpPort = 587 }), "change"));
        var pending = await Assert.ThrowsAsync<ConflictException>(() => Svc(_notifAdmin, s => s.ProposeAsync(NotificationChannel.Email, Json(new { provider = "Smtp", fromAddress = "third@acme.com", smtpHost = "smtp.acme.com", smtpPort = 587 }), "more")));
        Assert.Equal("change_pending", pending.Code);
    }

    // --- delivery -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Email_goes_to_the_person_in_their_language_with_an_absolute_link_and_only_once()
    {
        await ActivateEmailAsync();
        await ApprovalWaiting();
        await Sweep();
        var first = await Deliver();
        Assert.Equal(1, first.Sent);

        var mail = Assert.Single(_email.Sent);
        Assert.Equal("a1@t.com", mail.To);
        Assert.Equal("noreply@acme.com", mail.FromAddress);
        Assert.StartsWith("Approval needed:", mail.Subject);
        Assert.Contains("https://app.acme.com/approvals/", mail.TextBody);
        Assert.Contains("https://app.acme.com/approvals/", mail.HtmlBody);
        Assert.Contains("dir=\"ltr\"", mail.HtmlBody);

        Assert.Equal(0, (await Deliver()).Processed);
        await Sweep();
        Assert.Equal(0, (await Deliver()).Processed);
        Assert.Single(_email.Sent);
        Assert.Equal(DeliveryStatus.Sent, Assert.Single(await Deliveries()).Status);
    }

    [Fact]
    public async Task Email_is_written_in_the_recipients_own_language_and_direction()
    {
        await ActivateEmailAsync();
        await W.AsAsync(W.TenantA, A1, async s => await s.GetRequiredService<Vpms.Identity.Contracts.IUserPreferences>().SetMineAsync("ar", "hijri"));
        await ApprovalWaiting();
        await Sweep();
        await Deliver();

        var mail = Assert.Single(_email.Sent);
        Assert.StartsWith("مطلوب اعتمادك:", mail.Subject);
        Assert.Contains("dir=\"rtl\"", mail.HtmlBody);
        Assert.Matches(@"\p{IsArabic}", mail.TextBody);
    }

    [Fact]
    public async Task Preferences_are_saved_per_person_checked_and_audited()
    {
        var prefs = await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.Identity.Contracts.IUserPreferences>().GetAsync(A1));
        Assert.Equal(new Vpms.Identity.Contracts.UserPreferencesDto("en", "gregorian"), prefs);
        await Assert.ThrowsAsync<ValidationFailedException>(() => W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.Identity.Contracts.IUserPreferences>().SetMineAsync("fr", "gregorian")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.Identity.Contracts.IUserPreferences>().SetMineAsync("ar", "lunar")));
        await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.Identity.Contracts.IUserPreferences>().SetMineAsync("ar", "hijri"));
        Assert.Equal("ar", (await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<Vpms.Identity.Contracts.IUserPreferences>().GetAsync(A1))).Locale);
        Assert.Equal("en", (await W.AsAsync(W.TenantA, A2, s => s.GetRequiredService<Vpms.Identity.Contracts.IUserPreferences>().GetAsync(A2))).Locale);
        var audited = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.CountAsync(e => e.Action == "user.preferences.changed"));
        Assert.Equal(1, audited);
    }

    [Fact]
    public async Task Nothing_is_queued_for_a_channel_that_is_not_configured()
    {
        await ApprovalWaiting();
        await Sweep();
        Assert.Empty(await Deliveries());
        Assert.Equal(0, (await Deliver()).Processed);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task A_person_can_switch_email_off_for_an_optional_kind_but_never_for_a_mandatory_one()
    {
        await ActivateEmailAsync();
        await W.AsAsync(W.TenantA, PayApprover, s => s.GetRequiredService<INotificationService>().SetPreferenceAsync(NotificationKinds.PaymentAwaitingApproval, NotificationChannel.Email, false));
        var proposal = await DraftProposal();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        await ApprovalWaiting("INV-77");
        await Sweep();
        await Deliver();

        Assert.DoesNotContain(_email.Sent, m => m.To == "payappr@t.com");                  // opted out of this kind
        Assert.Contains(_email.Sent, m => m.To == "a1@t.com");                             // mandatory approval notice still sent
    }

    [Fact]
    public async Task A_temporary_failure_is_retried_later_and_a_permanent_one_goes_to_the_dead_letter()
    {
        await ActivateEmailAsync();
        await ApprovalWaiting();
        await Sweep();

        _email.Script.Enqueue(SendResult.Retry("smtp_unavailable"));
        var first = await Deliver();
        Assert.Equal(1, first.Retrying);
        var waiting = Assert.Single(await Deliveries());
        Assert.Equal(DeliveryStatus.Pending, waiting.Status);
        Assert.Equal("smtp_unavailable", waiting.LastError);
        Assert.Equal(0, (await Deliver()).Processed);                                      // too early

        Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, (await Deliver()).Sent);
        Assert.Single(_email.Sent);

        await ApprovalWaiting("INV-2");
        await Sweep();
        _email.Script.Enqueue(SendResult.Fail("smtp_550"));
        var dead = await Deliver();
        Assert.Equal(1, dead.DeadLettered);
        var summary = await Svc(_notifAdmin, s => s.SummaryAsync());
        Assert.Equal(1, summary.DeadLetter);
        var letters = await Svc(_notifAdmin, s => s.DeadLettersAsync(10));
        Assert.Equal("smtp_550", Assert.Single(letters).LastError);
    }

    [Fact]
    public async Task A_person_without_an_email_address_is_skipped_not_failed()
    {
        await ActivateEmailAsync();
        await ApprovalWaiting();
        await Sweep();
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            var db = s.GetRequiredService<Vpms.Identity.Infrastructure.IdentityDbContext>();
            (await db.Users.SingleAsync(u => u.Id == A1)).Email = "";
            return await db.SaveChangesAsync();
        });
        var run = await Deliver();
        Assert.Equal(1, run.Skipped);
        var delivery = Assert.Single(await Deliveries());
        Assert.Equal(DeliveryStatus.Skipped, delivery.Status);
        Assert.Equal("no_email_address", delivery.LastError);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task The_webhook_gets_one_message_per_event_for_the_chosen_kinds_not_one_per_recipient()
    {
        await ActivateWebhookAsync(NotificationKinds.PaymentAwaitingApproval);
        var second = await W.UserAsync(W.TenantA, "payappr3@t.com", null, "Payment Approver");
        Assert.NotEqual(Guid.Empty, second);
        var proposal = await DraftProposal();
        await Props(Preparer, s => s.SubmitAsync(proposal.Id));
        await ApprovalWaiting("INV-5"); // a kind that is not chosen
        await Sweep();
        await Deliver();

        var message = Assert.Single(_hook.Sent);
        Assert.StartsWith("Payment proposal to approve:", message.Title);
        Assert.StartsWith("https://app.acme.com/payments/proposals/", message.Url);
        await Sweep();
        await Deliver();
        Assert.Single(_hook.Sent);
    }

    [Fact]
    public async Task Another_tenants_channel_settings_do_not_apply_here()
    {
        var adminB = await W.UserAsync(W.TenantB, "nadminb@t.com", null, "Notification Administrator");
        var approverB = await W.UserAsync(W.TenantB, "napprb@t.com", null, "Notification Change Approver");
        var c = await W.AsAsync(W.TenantB, adminB, s => s.GetRequiredService<INotificationChannelService>().ProposeAsync(NotificationChannel.Email, Json(new { provider = "Smtp", fromAddress = "b@b.com", smtpHost = "smtp.b.com", smtpPort = 587 }), "B email"));
        await W.AsAsync(W.TenantB, approverB, s => s.GetRequiredService<INotificationChannelService>().ApproveAsync(c.Id, null));

        await ApprovalWaiting();
        await Sweep();
        Assert.Empty(await Deliveries());
        Assert.Null((await Svc(_notifAdmin, s => s.GetAsync(NotificationChannel.Email))).Active);
        Assert.Empty(await Svc(_notifApprover, s => s.ListPendingAsync()));
    }
}
