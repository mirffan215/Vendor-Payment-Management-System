using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class PreferencesApiTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public PreferencesApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Language_and_calendar_are_kept_on_the_server_and_come_back_with_the_session()
    {
        var client = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-pref", "pref@acme.com", "Pref");
        await AuthenticationTests.SignInAsync(client, _api.IdpA, _api.ProviderA, "/");
        var session = await client.GetFromJsonAsync<JsonElement>("/api/v1/session");
        var csrf = session.GetProperty("csrfToken").GetString()!;
        Assert.Equal("en", session.GetProperty("locale").GetString());
        Assert.Equal("gregorian", session.GetProperty("calendar").GetString());

        async Task<HttpResponseMessage> Put(object body)
        {
            var r = new HttpRequestMessage(HttpMethod.Put, "/api/v1/session/preferences") { Content = JsonContent.Create(body) };
            r.Headers.Add("X-XSRF-TOKEN", csrf);
            return await client.SendAsync(r);
        }

        Assert.Equal(HttpStatusCode.OK, (await Put(new { locale = "ar", calendar = "hijri" })).StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/session");
        Assert.Equal("ar", after.GetProperty("locale").GetString());
        Assert.Equal("hijri", after.GetProperty("calendar").GetString());

        var bad = await Put(new { locale = "xx", calendar = "hijri" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_locale", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal("invalid_calendar", (await (await Put(new { locale = "en", calendar = "lunar" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal("ar", (await client.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("locale").GetString());
    }
}
