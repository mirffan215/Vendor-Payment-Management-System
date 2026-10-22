using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

/// <summary>
/// Calls every controller action: with no session it must answer 401, and with a session that holds no role it must never succeed (outside a short list
/// of things every signed-in person needs) and never crash. A new action that forgets its check shows up here without anyone remembering to test it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class EndpointSweepTests : IClassFixture<ApiFixtureHolder>
{
    /// <summary>What a person with no role may do, with the reason. Everything else must be refused.</summary>
    private static readonly Dictionary<string, string> OpenToAnySignedInUser = new()
    {
        ["GET api/v1/session"] = "who am I, language and calendar, the anti-forgery token",
        ["GET api/v1/notifications"] = "own notifications",
        ["GET api/v1/notifications/unread-count"] = "own notifications",
        ["POST api/v1/notifications/read-all"] = "own notifications",
        ["GET api/v1/notifications/preferences"] = "own preferences",
        ["GET api/v1/approval-materiality/fields"] = "static list of field names",
        ["GET api/v1/verification-rules/catalog"] = "static catalogue",
        ["GET api/v1/admin/tenant"] = "own organisation name",
        ["GET api/v1/approvals/inbox"] = "own tasks (empty without a role)",
        ["GET api/v1/delegations/mine"] = "own delegations",
        ["GET api/v1/approvals/notices"] = "own notices",
        ["GET api/v1/legal-entities"] = "entities the person may see (none without a role)",
    };

    private readonly ApiFixture _api;

    public EndpointSweepTests(ApiFixtureHolder holder) => _api = holder.Api;

    private IEnumerable<(string Method, string Template, string Url, bool Anonymous)> Routes()
    {
        var provider = _api.Services.GetRequiredService<IActionDescriptorCollectionProvider>();
        foreach (var action in provider.ActionDescriptors.Items)
        {
            var template = action.AttributeRouteInfo?.Template;
            if (template is null) continue;
            var methods = action.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods).ToArray() ?? [];
            var anonymous = action.EndpointMetadata.OfType<IAllowAnonymous>().Any();
            foreach (var method in methods)
            {
                yield return (method, template, Fill(template), anonymous);
            }
        }
    }

    private static string Fill(string template)
    {
        var path = Regex.Replace(template, @"\{version:apiVersion\}", "1");
        path = Regex.Replace(path, @"\{[^}:]+:guid\}", _ => Guid.NewGuid().ToString());
        path = Regex.Replace(path, @"\{[^}:]+:int\}", "1");
        return "/" + Regex.Replace(path, @"\{[^}]+\}", "x");
    }

    [Fact]
    public void The_sweep_sees_the_whole_api()
    {
        var routes = Routes().ToList();
        Assert.True(routes.Count > 150, $"only {routes.Count} routes found");
        Assert.Contains(routes, r => r.Method == "POST" && r.Template.Contains("/reports", StringComparison.Ordinal) is false && r.Template.Contains("erp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_session_every_protected_action_answers_401()
    {
        var browser = _api.NewBrowser();
        var wrong = new List<string>();
        foreach (var (method, template, url, anonymous) in Routes().Where(r => !r.Anonymous))
        {
            var response = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = Body(method) });
            if (response.StatusCode != HttpStatusCode.Unauthorized) wrong.Add($"{method} {template} -> {(int)response.StatusCode}");
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public async Task A_signed_in_person_with_no_role_gets_nothing_and_breaks_nothing()
    {
        var email = "sweep-norole@acme.com";
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-" + email, email, "sweep");
        await AuthenticationTests.SignInAsync(browser, _api.IdpA, _api.ProviderA, "/");
        var csrf = (await browser.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("csrfToken").GetString()!;

        var open = new List<string>();
        var crashed = new List<string>();
        foreach (var (method, template, url, anonymous) in Routes().Where(r => !r.Anonymous))
        {
            // Ending the person's own sessions would stop the rest of the sweep (it has its own test).
            if (template.EndsWith("session/revoke-all", StringComparison.Ordinal)) continue;
            var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = Body(method) };
            request.Headers.Add("X-XSRF-TOKEN", csrf);
            var response = await browser.SendAsync(request);
            var key = $"{method} {template.Replace("v{version:apiVersion}", "v1", StringComparison.Ordinal)}";
            if ((int)response.StatusCode >= 500) crashed.Add($"{key} -> {(int)response.StatusCode}");
            else if ((int)response.StatusCode is >= 200 and < 300 && !OpenToAnySignedInUser.ContainsKey(key)) open.Add($"{key} -> {(int)response.StatusCode}");
        }

        Assert.True(crashed.Count == 0, "Crashed: " + string.Join("; ", crashed));
        Assert.True(open.Count == 0, "Succeeded without any role: " + string.Join("; ", open));
    }

    private static HttpContent? Body(string method) => method is "POST" or "PUT" or "PATCH" ? JsonContent.Create(new { }) : null;
}
