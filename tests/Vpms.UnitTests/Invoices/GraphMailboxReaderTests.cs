using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vpms.Invoices.Infrastructure;

namespace Vpms.UnitTests.Invoices;

/// <summary>The Graph adapter against a scripted HTTP handler. It is not verified against a live mailbox.</summary>
public class GraphMailboxReaderTests
{
    private static readonly GraphOptions Configured = new() { TenantId = "tenant-1", ClientId = "client-1", ClientSecret = "s3cret" };

    private sealed class Script : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? Body)> Requests { get; } = [];
        public List<string> RawUrls { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RawUrls.Add(request.RequestUri!.AbsoluteUri);
            Requests.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return Respond(request);
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static (GraphMailboxReader Reader, Script Script, FakeTimeProvider Clock) Build(GraphOptions? options = null)
    {
        var script = new Script();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));
        script.Respond = r =>
        {
            var url = r.RequestUri!.AbsoluteUri;
            if (url.Contains("/oauth2/v2.0/token", StringComparison.Ordinal)) return Json(new { access_token = "tok-" + clock.GetUtcNow().Minute, expires_in = 3600 });
            if (url.Contains("/attachments", StringComparison.Ordinal))
            {
                return Json(new
                {
                    value = new object[]
                    {
                        new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = "inv.pdf", ["contentType"] = "application/pdf", ["size"] = 8, ["contentBytes"] = Convert.ToBase64String("%PDF-1.7"u8.ToArray()) },
                        new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.itemAttachment", ["name"] = "forwarded mail", ["size"] = 5 },
                        new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = "huge.pdf", ["contentType"] = "application/pdf", ["size"] = 999_999_999, ["contentBytes"] = "AAAA" },
                    },
                });
            }

            if (url.Contains("/messages?", StringComparison.Ordinal))
            {
                return Json(new { value = new[] { new { id = "AAMk=1", subject = "Invoice 42", from = new { emailAddress = new { address = "ap@vendor.example" } }, receivedDateTime = "2026-10-20T09:15:00Z" } } });
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        return (new GraphMailboxReader(new HttpClient(script), Options.Create(options ?? Configured), clock, NullLogger<GraphMailboxReader>.Instance), script, clock);
    }

    [Fact]
    public async Task Reads_unread_mail_with_file_attachments_using_an_app_token()
    {
        var (reader, script, _) = Build();
        var mails = await reader.FetchUnprocessedAsync("invoices@acme.example", 10);

        var mail = Assert.Single(mails);
        Assert.Equal("AAMk=1", mail.Id);
        Assert.Equal("ap@vendor.example", mail.From);
        Assert.Equal("Invoice 42", mail.Subject);
        var file = Assert.Single(mail.Attachments); // item attachments and oversized files are not taken
        Assert.Equal("inv.pdf", file.FileName);
        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(file.Content));

        var token = script.Requests[0];
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token", token.Url);
        Assert.Contains("grant_type=client_credentials", token.Body);
        Assert.Contains("scope=https://graph.microsoft.com/.default", Uri.UnescapeDataString(token.Body!));

        var list = script.Requests[1];
        Assert.Equal("Bearer tok-0", list.Auth);
        Assert.Contains("/users/invoices@acme.example/mailFolders/inbox/messages?", list.Url);
        Assert.Contains("isRead eq false and hasAttachments eq true", list.Url);
        Assert.Contains("$orderby=receivedDateTime asc", list.Url);
        Assert.Contains("$top=10", list.Url);
    }

    [Fact]
    public async Task Marks_handled_mail_as_read()
    {
        var (reader, script, _) = Build();
        await reader.MarkProcessedAsync("invoices@acme.example", "AAMk=1");
        var patch = script.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Contains("/users/invoices@acme.example/messages/AAMk=1", patch.Url);
        Assert.Contains("\"isRead\":true", patch.Body);
        Assert.StartsWith("Bearer ", patch.Auth);
    }

    [Fact]
    public async Task Caches_the_token_until_shortly_before_it_expires()
    {
        var (reader, script, clock) = Build();
        await reader.FetchUnprocessedAsync("a@acme.example", 5);
        await reader.FetchUnprocessedAsync("a@acme.example", 5);
        Assert.Single(script.Requests, r => r.Url.Contains("/oauth2/", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromMinutes(59));
        await reader.FetchUnprocessedAsync("a@acme.example", 5);
        Assert.Equal(2, script.Requests.Count(r => r.Url.Contains("/oauth2/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Does_nothing_until_configured()
    {
        var (reader, script, _) = Build(new GraphOptions());
        Assert.Empty(await reader.FetchUnprocessedAsync("a@acme.example", 5));
        await reader.MarkProcessedAsync("a@acme.example", "x");
        Assert.Empty(script.Requests);
    }

    [Fact]
    public async Task Graph_errors_surface_so_the_run_is_recorded_as_failed()
    {
        var (reader, script, _) = Build();
        var original = script.Respond;
        script.Respond = r => r.RequestUri!.AbsoluteUri.Contains("/messages?", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Forbidden) : original(r);
        await Assert.ThrowsAsync<HttpRequestException>(() => reader.FetchUnprocessedAsync("a@acme.example", 5));
    }

    [Fact]
    public async Task Mailbox_and_ids_are_escaped_in_urls()
    {
        var (reader, script, _) = Build();
        await reader.MarkProcessedAsync("a b@acme.example", "id/with?odd#chars");
        var raw = script.RawUrls.Last();
        Assert.Contains("a%20b%40acme.example", raw);
        Assert.Contains("id%2Fwith%3Fodd%23chars", raw);
    }
}
