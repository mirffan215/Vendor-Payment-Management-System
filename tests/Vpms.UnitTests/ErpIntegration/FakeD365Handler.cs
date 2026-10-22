using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vpms.UnitTests.ErpIntegration;

/// <summary>
/// A small stand-in for a Dynamics 365 Finance environment: the Entra token endpoint and the handful of OData entity sets the adapter uses,
/// with equality filters, paging, generated keys and the refusals D365 gives (unknown vendor). It is a stub written from the documented
/// OData behaviour, NOT a recording of a real environment.
/// </summary>
public sealed class FakeD365Handler : HttpMessageHandler
{
    public const string Secret = "secret";

    private readonly object _gate = new();
    private readonly Dictionary<string, List<JsonObject>> _sets = new();
    private int _sequence;
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _faults = new();

    private readonly Dictionary<int, Func<HttpRequestMessage, HttpResponseMessage>> _faultAt = new();
    private int _dataRequests;

    /// <summary>The n-th data request from now (1 = the next one) is answered with this instead of working.</summary>
    public void FaultOnRequest(int n, Func<HttpRequestMessage, HttpResponseMessage> response) { lock (_gate) _faultAt[_dataRequests + n] = response; }

    public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

    public int TokenCalls { get; private set; }

    public Exception? ThrowOnNext { get; set; }

    public int PageSize { get; set; } = 1000;

    public string TokenResponse { get; set; } = """{"access_token":"fake-token","expires_in":3600}""";

    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    public IReadOnlyList<JsonObject> Set(string name) { lock (_gate) return _sets.GetValueOrDefault(name)?.ToList() ?? []; }

    public void Add(string set, JsonObject row) { lock (_gate) Rows(set).Add(row); }

    /// <summary>The next data request is answered with this instead of working.</summary>
    public void Fault(Func<HttpRequestMessage, HttpResponseMessage> response) { lock (_gate) _faults.Enqueue(response); }

    public static HttpResponseMessage Status(HttpStatusCode code, string? message = null, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(message is null ? "" : new JsonObject { ["error"] = new JsonObject { ["message"] = message } }.ToJsonString(), Encoding.UTF8, "application/json") };
        if (retryAfter is { } ra) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
        return r;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate) Requests.Add((request.Method, request.RequestUri!.ToString(), body));

        if (ThrowOnNext is { } ex)
        {
            ThrowOnNext = null;
            throw ex;
        }

        if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            TokenCalls++;
            var form = body ?? "";
            if (TokenStatus != HttpStatusCode.OK) return new HttpResponseMessage(TokenStatus);
            if (!form.Contains("client_secret=" + Secret, StringComparison.Ordinal)) return Status(HttpStatusCode.Unauthorized);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TokenResponse, Encoding.UTF8, "application/json") };
        }

        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: "fake-token" }) return Status(HttpStatusCode.Unauthorized, "Not signed in");
        lock (_gate)
        {
            _dataRequests++;
            if (_faultAt.Remove(_dataRequests, out var at)) return at(request);
            if (_faults.Count > 0) return _faults.Dequeue()(request);
            return Handle(request, body);
        }
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, string? body)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var dataAt = path.IndexOf("/data/", StringComparison.Ordinal);
        if (dataAt < 0) return Status(HttpStatusCode.NotFound);
        var resource = Uri.UnescapeDataString(path[(dataAt + 6)..]);
        var query = ParseQuery(uri.Query);

        if (request.Method == HttpMethod.Get) return List(resource, query, uri);
        if (request.Method == HttpMethod.Patch) return Patch(resource, body);
        if (request.Method == HttpMethod.Post) return Create(resource, JsonNode.Parse(body ?? "{}")!.AsObject());
        return Status(HttpStatusCode.MethodNotAllowed);
    }

    private HttpResponseMessage List(string set, Dictionary<string, string> query, Uri uri)
    {
        IEnumerable<JsonObject> rows = set == "VendorInvoiceHeaders" ? Invoices() : Rows(set);
        if (query.TryGetValue("$filter", out var filter))
        {
            foreach (var clause in filter.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Regex.Match(clause.Trim(), @"^(\w+) eq '((?:[^']|'')*)'$");
                if (!m.Success) continue; // ge/le clauses are not emulated
                var field = m.Groups[1].Value;
                var value = m.Groups[2].Value.Replace("''", "'");
                rows = rows.Where(r => r[field]?.ToString() == value);
            }
        }

        var all = rows.ToList();
        var skip = query.TryGetValue("$skip", out var s) ? int.Parse(s) : 0;
        var top = query.TryGetValue("$top", out var t) ? Math.Min(int.Parse(t), PageSize) : PageSize;
        var page = all.Skip(skip).Take(top).ToList();
        var result = new JsonObject { ["value"] = new JsonArray(page.Select(r => (JsonNode)r.DeepClone()).ToArray()) };
        if (skip + page.Count < all.Count)
        {
            var next = Regex.Replace(uri.ToString(), @"[&?]\$skip=\d+", "");
            result["@odata.nextLink"] = next + (next.Contains('?') ? "&" : "?") + "$skip=" + (skip + page.Count);
        }

        return Json(HttpStatusCode.OK, result);
    }

    private HttpResponseMessage Create(string set, JsonObject row)
    {
        switch (set)
        {
            case "VendorsV2":
                row["VendorAccountNumber"] = $"V-{++_sequence:000}";
                break;
            case "VendorInvoiceJournalHeaders":
                row["JournalBatchNumber"] = $"JB-{++_sequence:000}";
                break;
            case "VendorInvoiceJournalLines":
                var account = row["Account"]?.ToString();
                if (!Rows("VendorsV2").Any(v => v["VendorAccountNumber"]?.ToString() == account))
                {
                    return Status(HttpStatusCode.BadRequest, $"Vendor account {account} does not exist.");
                }

                break;
        }

        Rows(set).Add(row);
        return Json(HttpStatusCode.Created, row);
    }

    private HttpResponseMessage Patch(string resource, string? body)
    {
        var m = Regex.Match(resource, @"^(\w+)\(dataAreaId='([^']*)',VendorAccountNumber='([^']*)'\)$");
        if (!m.Success) return Status(HttpStatusCode.NotFound);
        var row = Rows(m.Groups[1].Value).FirstOrDefault(v => v["VendorAccountNumber"]?.ToString() == m.Groups[3].Value);
        if (row is null) return Status(HttpStatusCode.NotFound, "Vendor not found.");
        foreach (var (k, v) in JsonNode.Parse(body ?? "{}")!.AsObject()) row[k] = v?.DeepClone();
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    /// <summary>What D365 shows as posted vendor invoices: one per journal, built from its lines.</summary>
    private IEnumerable<JsonObject> Invoices()
    {
        foreach (var header in Rows("VendorInvoiceJournalHeaders").ToList())
        {
            var batch = header["JournalBatchNumber"]!.ToString();
            var lines = Rows("VendorInvoiceJournalLines").Where(l => l["JournalBatchNumber"]?.ToString() == batch).ToList();
            if (lines.Count == 0) continue;
            var first = lines[0];
            yield return new JsonObject
            {
                ["dataAreaId"] = header["dataAreaId"]?.DeepClone(),
                ["JournalBatchNumber"] = batch,
                ["Description"] = header["Description"]?.DeepClone(),
                ["InvoiceNumber"] = first["Invoice"]?.DeepClone(),
                ["InvoiceAccount"] = first["Account"]?.DeepClone(),
                ["InvoiceAmount"] = lines.Sum(l => decimal.Parse(l["CreditAmount"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture)),
                ["CurrencyCode"] = first["CurrencyCode"]?.DeepClone(),
                ["InvoiceDate"] = first["TransactionDate"]?.DeepClone(),
                ["InvoiceStatus"] = header["InvoiceStatus"]?.ToString() ?? "Posted",
            };
        }
    }

    private List<JsonObject> Rows(string set)
    {
        if (!_sets.TryGetValue(set, out var list)) _sets[set] = list = [];
        return list;
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace("+", " ")) : "");

    private static HttpResponseMessage Json(HttpStatusCode code, JsonNode node) => new(code) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}
