using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vpms.Notifications.Application;
using Vpms.Notifications.Domain;
using Vpms.Notifications.Infrastructure;

namespace Vpms.UnitTests;

public sealed class NotificationSendersTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString()));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body = "{}") => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static readonly EmailChannelConfig GraphSettings = new("Graph", "noreply@acme.com", "Acme AP", "tenant-1", "client-1", "graph-secret", "ap@acme.com", null, 0, false, null);
    private static readonly OutgoingEmail Mail = new("alice@acme.com", "Approval needed: INV-1", "<p>Hi &lt;b&gt;</p>", "Hi", "noreply@acme.com", "Acme AP");

    private static GraphEmailSender Graph(Stub handler) => new(new HttpClient(handler), Options.Create(new GraphMailOptions()), new FakeTimeProvider(DateTimeOffset.Parse("2026-10-01T08:00:00Z")));

    [Fact]
    public async Task Graph_signs_in_once_and_posts_the_message_as_the_mailbox_with_the_secret_only_at_sign_in()
    {
        var handler = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/token") ? Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":3600}""") : new HttpResponseMessage(HttpStatusCode.Accepted));
        var sender = Graph(handler);
        var json = ChannelRules.ToJson(GraphSettings);
        Assert.Equal(SendOutcome.Sent, (await sender.SendAsync(json, "s3cret", Mail)).Outcome);
        Assert.Equal(SendOutcome.Sent, (await sender.SendAsync(json, "s3cret", Mail)).Outcome);

        Assert.Equal(1, handler.Requests.Count(r => r.Url.EndsWith("/oauth2/v2.0/token")));
        var send = handler.Requests.Last();
        Assert.Equal("https://graph.microsoft.com/v1.0/users/ap%40acme.com/sendMail", send.Url);
        Assert.Equal("Bearer tok", send.Auth);
        var body = JsonNode.Parse(send.Body!)!;
        Assert.Equal("alice@acme.com", body["message"]!["toRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>());
        Assert.Equal("HTML", body["message"]!["body"]!["contentType"]!.GetValue<string>());
        Assert.All(handler.Requests.Where(r => !r.Url.Contains("/token")), r => Assert.DoesNotContain("s3cret", r.Body ?? ""));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, SendOutcome.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SendOutcome.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, SendOutcome.Transient)]
    [InlineData(HttpStatusCode.Forbidden, SendOutcome.Transient)]
    [InlineData(HttpStatusCode.BadRequest, SendOutcome.Permanent)]
    [InlineData(HttpStatusCode.NotFound, SendOutcome.Permanent)]
    public async Task Graph_failures_are_sorted_into_retry_or_give_up(HttpStatusCode status, SendOutcome expected)
    {
        var handler = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/token") ? Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":3600}""") : new HttpResponseMessage(status));
        Assert.Equal(expected, (await Graph(handler).SendAsync(ChannelRules.ToJson(GraphSettings), "s", Mail)).Outcome);
    }

    [Fact]
    public async Task Graph_without_a_secret_retries_and_a_refused_sign_in_retries_without_leaking_it()
    {
        var none = await Graph(new Stub(_ => Json(HttpStatusCode.OK))).SendAsync(ChannelRules.ToJson(GraphSettings), null, Mail);
        Assert.Equal(SendOutcome.Transient, none.Outcome);
        Assert.Equal("secret_unavailable", none.Code);

        var refused = await Graph(new Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))).SendAsync(ChannelRules.ToJson(GraphSettings), "sec", Mail);
        Assert.Equal(SendOutcome.Transient, refused.Outcome);
        Assert.DoesNotContain("sec", refused.Code ?? "");
    }

    private static TeamsWebhookSender Teams(Stub handler, bool internalOk = false) =>
        new(new HttpClient(handler), Options.Create(new NotificationOptions { AllowInternalHosts = internalOk }));

    [Fact]
    public async Task The_webhook_posts_an_adaptive_card_with_title_text_and_link()
    {
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        var result = await Teams(handler).SendAsync("https://contoso.webhook.office.com/webhookb2/abc", new WebhookMessage("ERP message needs attention", "Details", "https://app/erp/sync"));
        Assert.Equal(SendOutcome.Sent, result.Outcome);
        var card = JsonNode.Parse(handler.Requests.Single().Body!)!["attachments"]![0]!["content"]!;
        Assert.Equal("AdaptiveCard", card["type"]!.GetValue<string>());
        Assert.Equal("ERP message needs attention", card["body"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("https://app/erp/sync", card["actions"]![0]!["url"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://contoso.webhook.office.com/x")]
    [InlineData("https://evil.example.com/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://user:pw@contoso.webhook.office.com/x")]
    [InlineData("https://contoso.webhook.office.com.evil.com/x")]
    public async Task A_webhook_address_that_is_not_allowed_is_never_called(string url)
    {
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await Teams(handler).SendAsync(url, new WebhookMessage("t", "b", null));
        Assert.Equal(SendOutcome.Permanent, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, SendOutcome.Transient)]
    [InlineData(HttpStatusCode.BadGateway, SendOutcome.Transient)]
    [InlineData(HttpStatusCode.BadRequest, SendOutcome.Permanent)]
    public async Task Webhook_failures_are_sorted_into_retry_or_give_up(HttpStatusCode status, SendOutcome expected) =>
        Assert.Equal(expected, (await Teams(new Stub(_ => new HttpResponseMessage(status))).SendAsync("https://contoso.webhook.office.com/x", new WebhookMessage("t", "b", null))).Outcome);

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("mail.internal", false)]
    [InlineData("smtp.office365.com", true)]
    [InlineData("203.0.113.7", true)]
    [InlineData("not a host", false)]
    [InlineData("", false)]
    public void Mail_servers_must_not_point_inside_the_network(string host, bool allowed) => Assert.Equal(allowed, ChannelRules.IsAllowedHost(host, false));

    [Fact]
    public void Channel_settings_are_checked()
    {
        Assert.Empty(GraphSettings.Validate(false));
        Assert.Contains((GraphSettings with { FromAddress = "not-an-address" }).Validate(false), i => i.Code == "from_invalid");
        Assert.Contains((GraphSettings with { SecretRef = "a secret with spaces" }).Validate(false), i => i.Code == "secret_ref_invalid");
        Assert.Contains((GraphSettings with { Provider = "Carrier" }).Validate(false), i => i.Code == "provider_unknown");
        var smtp = new EmailChannelConfig("Smtp", "noreply@acme.com", null, null, null, "smtp-pass", null, "smtp.office365.com", 587, true, "user");
        Assert.Empty(smtp.Validate(false));
        Assert.Contains((smtp with { SmtpHost = "10.0.0.5" }).Validate(false), i => i.Code == "smtp_host_invalid");
        Assert.Empty((smtp with { SmtpHost = "10.0.0.5" }).Validate(true));
        Assert.Contains((smtp with { SmtpPort = 0 }).Validate(false), i => i.Code == "smtp_port_invalid");

        var hook = new WebhookChannelConfig("https://x.webhook.office.com/a", ["ErpDeadLetter"]);
        Assert.Empty(hook.Validate(false, [".webhook.office.com"]));
        Assert.Contains((hook with { Kinds = [] }).Validate(false, [".webhook.office.com"]), i => i.Code == "kinds_invalid");
        Assert.Contains((hook with { Kinds = ["Nope"] }).Validate(false, [".webhook.office.com"]), i => i.Code == "kinds_invalid");
        Assert.Contains((hook with { Url = "https://evil.com/a" }).Validate(false, [".webhook.office.com"]), i => i.Code == "webhook_host_not_allowed");
    }

    // --- SMTP against a tiny in-process server ----------------------------------------------------------------

    private sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Lines { get; } = [];
        public string Data { get; private set; } = "";
        public Task Serving { get; private set; } = Task.CompletedTask;

        public FakeSmtp(string? rejectRecipientWith = null)
        {
            _listener.Start();
            Serving = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake ESMTP");
                var data = new StringBuilder();
                while (await reader.ReadLineAsync() is { } line)
                {
                    Lines.Add(line);
                    var cmd = line.ToUpperInvariant();
                    if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await writer.WriteLineAsync("250 fake");
                    else if (cmd.StartsWith("MAIL FROM")) await writer.WriteLineAsync("250 ok");
                    else if (cmd.StartsWith("RCPT TO")) await writer.WriteLineAsync(rejectRecipientWith ?? "250 ok");
                    else if (cmd == "DATA")
                    {
                        await writer.WriteLineAsync("354 go");
                        while (await reader.ReadLineAsync() is { } d && d != ".") data.AppendLine(d);
                        Data = data.ToString();
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (cmd == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                    else await writer.WriteLineAsync("250 ok");
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task Smtp_delivers_the_message_with_both_text_and_html()
    {
        using var server = new FakeSmtp();
        var settings = new EmailChannelConfig("Smtp", "noreply@acme.com", "Acme AP", null, null, null, null, "127.0.0.1", server.Port, false, null);
        var result = await new SmtpEmailSender(NullLogger<SmtpEmailSender>.Instance).SendAsync(ChannelRules.ToJson(settings), null, Mail);
        await server.Serving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Contains(server.Lines, l => l.StartsWith("MAIL FROM:<noreply@acme.com>"));
        Assert.Contains(server.Lines, l => l.StartsWith("RCPT TO:<alice@acme.com>"));
        Assert.Contains("Subject: Approval needed: INV-1", server.Data);
        Assert.Contains("text/html", server.Data);
        Assert.Contains("text/plain", server.Data);
    }

    [Fact]
    public async Task Smtp_refusals_are_told_apart_from_outages()
    {
        using var refusing = new FakeSmtp("550 no such user");
        var settings = new EmailChannelConfig("Smtp", "noreply@acme.com", null, null, null, null, null, "127.0.0.1", refusing.Port, false, null);
        Assert.Equal(SendOutcome.Permanent, (await new SmtpEmailSender(NullLogger<SmtpEmailSender>.Instance).SendAsync(ChannelRules.ToJson(settings), null, Mail)).Outcome);

        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var down = settings with { SmtpPort = port };
        Assert.Equal(SendOutcome.Transient, (await new SmtpEmailSender(NullLogger<SmtpEmailSender>.Instance).SendAsync(ChannelRules.ToJson(down), null, Mail)).Outcome);
    }

    // --- delivery state --------------------------------------------------------------------------------------

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_delivery_retries_with_a_doubling_delay_then_dead_letters()
    {
        var d = new NotificationDelivery { NextAttemptAt = T0 };
        var now = T0;
        for (var i = 1; i < NotificationDelivery.MaxAttempts; i++)
        {
            d.Lease(now);
            d.FailTransient("smtp_unavailable", now, null);
            Assert.Equal(DeliveryStatus.Pending, d.Status);
            Assert.Equal(now + TimeSpan.FromMinutes(Math.Pow(2, i - 1)), d.NextAttemptAt);
            now = d.NextAttemptAt;
        }

        d.Lease(now);
        d.FailTransient("smtp_unavailable", now, null);
        Assert.Equal(DeliveryStatus.DeadLetter, d.Status);
        Assert.Contains("Gave up", d.LastError);
    }

    [Fact]
    public void A_delivery_cannot_be_taken_twice_and_a_retry_after_is_honoured()
    {
        var d = new NotificationDelivery { NextAttemptAt = T0 };
        d.Lease(T0);
        Assert.Throws<Vpms.BuildingBlocks.Errors.ConflictException>(() => d.Lease(T0.AddMinutes(1)));
        d.FailTransient("throttled", T0, TimeSpan.FromMinutes(20));
        Assert.Equal(T0.AddMinutes(20), d.NextAttemptAt);
    }
}
