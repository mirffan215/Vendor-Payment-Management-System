using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Vpms.ErpIntegration.Contracts;

namespace Vpms.ErpIntegration.Infrastructure.D365;

/// <summary>
/// Dynamics 365 Finance through its documented OData data entities, authenticated with Entra client credentials. It never touches the
/// D365 database. Failures are classified, not thrown: throttling and server errors are Transient (with Retry-After), a refusal by D365
/// is Rejected. Every create looks for a document carrying the idempotency reference first, so a replay creates nothing.
/// </summary>
public sealed class D365FinanceAdapter(HttpClient http, IOptions<D365Options> options, TimeProvider clock) : IErpAdapter
{
    public const string ProviderName = "D365Finance";

    private readonly object _tokenGate = new();
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new();

    public string Provider => ProviderName;

    public string DocumentReference(string idempotencyKey) => Reference(idempotencyKey);

    // --- connection ------------------------------------------------------------------------------------------

    public async Task<ErpResult> ValidateConnectionAsync(ErpContext context, CancellationToken ct = default)
    {
        var probe = await GetAsync(context, Set(context, "vendors"), "$top=1&$select=VendorAccountNumber", ct);
        return probe.Failure ?? ErpResult.Ok("connected");
    }

    // --- vendors ---------------------------------------------------------------------------------------------

    public async Task<ErpResult> UpsertVendorAsync(ErpContext context, ErpVendor vendor, string idempotencyKey, CancellationToken ct = default)
    {
        var set = Set(context, "vendors");
        var company = context.Company.Company;
        var body = new JsonObject
        {
            ["dataAreaId"] = company,
            ["VendorOrganizationName"] = vendor.Name,
            ["AddressCountryRegionId"] = vendor.CountryCode,
            ["CurrencyCode"] = vendor.CurrencyCode,
        };
        if (!string.IsNullOrWhiteSpace(vendor.TaxNumber)) body["TaxRegistrationNumber"] = vendor.TaxNumber;
        if (!string.IsNullOrWhiteSpace(vendor.PaymentTermsCode) && context.Company.PaymentTermsMap.TryGetValue(vendor.PaymentTermsCode, out var terms)) body["PaymentTermsName"] = terms;

        if (!string.IsNullOrWhiteSpace(vendor.ExistingAccount))
        {
            var key = $"{set}(dataAreaId='{Q(company)}',VendorAccountNumber='{Q(vendor.ExistingAccount)}')";
            var patch = await SendAsync(context, HttpMethod.Patch, key, body, ct);
            return patch.Failure ?? ErpResult.Ok(vendor.ExistingAccount);
        }

        // A vendor created earlier under the same key (answer lost) is found by its stored reference, not created again.
        var reference = Reference(idempotencyKey);
        var found = await GetAsync(context, set, $"$filter=dataAreaId eq '{Q(company)}' and VendorSearchName eq '{Q(reference)}'&$top=1&$select=VendorAccountNumber", ct);
        if (found.Failure is not null) return found.Failure;
        if (found.Items.FirstOrDefault()?["VendorAccountNumber"]?.GetValue<string>() is { } existing) return ErpResult.Existing(existing);

        body["VendorSearchName"] = reference;
        var created = await SendAsync(context, HttpMethod.Post, set, body, ct);
        if (created.Failure is not null) return created.Failure;
        var account = created.Body?["VendorAccountNumber"]?.GetValue<string>();
        return string.IsNullOrEmpty(account) ? ErpResult.Rejected("bad_response", "D365 did not return the vendor account.") : ErpResult.Ok(account);
    }

    public async Task<ErpQuery<ErpVendorRecord>> ListVendorsAsync(ErpContext context, CancellationToken ct = default)
    {
        var page = await ListAllAsync(context, Set(context, "vendors"), $"$filter=dataAreaId eq '{Q(context.Company.Company)}'", ct);
        if (page.Failure is not null) return Fail<ErpVendorRecord>(page.Failure);
        return ErpQuery<ErpVendorRecord>.Of(page.Items.Select(v => new ErpVendorRecord(
            Str(v, "VendorAccountNumber"), Str(v, "VendorOrganizationName"), Opt(v, "TaxRegistrationNumber"), Str(v, "CurrencyCode"),
            Str(v, "OnHoldStatus") is not ("" or "No"))).ToList());
    }

    // --- invoices --------------------------------------------------------------------------------------------

    public async Task<ErpResult> CreateVendorInvoiceAsync(ErpContext context, ErpInvoice invoice, CancellationToken ct = default)
    {
        var headers = Set(context, "invoiceJournalHeaders");
        var lines = Set(context, "invoiceJournalLines");
        var company = context.Company.Company;
        var reference = Reference(invoice.DocumentKey);

        var existing = await GetAsync(context, headers, $"$filter=dataAreaId eq '{Q(company)}' and Description eq '{Q(reference)}'&$top=1&$select=JournalBatchNumber", ct);
        if (existing.Failure is not null) return existing.Failure;
        if (existing.Items.FirstOrDefault()?["JournalBatchNumber"]?.GetValue<string>() is { } found) return ErpResult.Existing(found);

        var created = await SendAsync(context, HttpMethod.Post, headers, new JsonObject
        {
            ["dataAreaId"] = company,
            ["JournalName"] = context.Company.JournalName,
            ["Description"] = reference,
        }, ct);
        if (created.Failure is not null) return created.Failure;
        var batch = created.Body?["JournalBatchNumber"]?.GetValue<string>();
        if (string.IsNullOrEmpty(batch)) return ErpResult.Rejected("bad_response", "D365 did not return the journal number.");

        foreach (var line in invoice.Lines)
        {
            var row = new JsonObject
            {
                ["dataAreaId"] = company,
                ["JournalBatchNumber"] = batch,
                ["LineNumber"] = line.LineNo,
                ["AccountType"] = "Vendor",
                ["Account"] = invoice.VendorAccount,
                ["Invoice"] = invoice.InvoiceNumber,
                ["TransactionDate"] = invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["CurrencyCode"] = invoice.CurrencyCode,
                ["CreditAmount"] = line.Amount,
                ["Text"] = Trim(line.Description, 60),
            };
            if (invoice.DueDate is { } due) row["DueDate"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(line.TaxCode)) row["ItemSalesTaxGroup"] = line.TaxCode;
            if (!string.IsNullOrWhiteSpace(context.Company.PostingProfile)) row["PostingProfile"] = context.Company.PostingProfile;
            if (line.Dimensions.Count > 0) row["DefaultDimensionDisplayValue"] = string.Join('-', line.Dimensions.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Value));
            var added = await SendAsync(context, HttpMethod.Post, lines, row, ct);
            if (added.Failure is not null) return added.Failure;
        }

        return ErpResult.Ok(batch);
    }

    public async Task<ErpResult> AttachDocumentAsync(ErpContext context, string invoiceReference, ErpAttachment attachment, string idempotencyKey, CancellationToken ct = default)
    {
        var set = Set(context, "invoiceAttachments");
        var company = context.Company.Company;
        var reference = Reference(idempotencyKey);

        var existing = await GetAsync(context, set, $"$filter=dataAreaId eq '{Q(company)}' and Name eq '{Q(reference)}'&$top=1&$select=Name", ct);
        if (existing.Failure is not null) return existing.Failure;
        if (existing.Items.Count > 0) return ErpResult.Existing(invoiceReference);

        var body = new JsonObject
        {
            ["dataAreaId"] = company,
            ["EntityName"] = Set(context, "invoiceJournalHeaders"),
            ["EntityKey"] = invoiceReference,
            ["Name"] = reference,
            ["FileName"] = attachment.FileName,
            ["ContentType"] = attachment.ContentType,
            ["FileContent"] = Convert.ToBase64String(attachment.Content),
        };
        var created = await SendAsync(context, HttpMethod.Post, set, body, ct);
        return created.Failure ?? ErpResult.Ok(invoiceReference);
    }

    public async Task<ErpQuery<ErpInvoiceStatus>> ListInvoicesAsync(ErpContext context, DateTimeOffset? changedSince, CancellationToken ct = default)
    {
        var filter = $"$filter=dataAreaId eq '{Q(context.Company.Company)}'";
        if (changedSince is { } since) filter += $" and ModifiedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        var page = await ListAllAsync(context, Set(context, "vendorInvoices"), filter, ct);
        if (page.Failure is not null) return Fail<ErpInvoiceStatus>(page.Failure);

        return ErpQuery<ErpInvoiceStatus>.Of(page.Items.Select(i => new ErpInvoiceStatus(
            Str(i, "InvoiceNumber") is { Length: > 0 } n ? $"{Str(i, "JournalBatchNumber")}|{n}".Trim('|') : Str(i, "JournalBatchNumber"),
            Opt(i, "Description"), Str(i, "InvoiceAccount"), Str(i, "InvoiceNumber"), Dec(i, "InvoiceAmount"), Str(i, "CurrencyCode"),
            Date(i, "InvoiceDate"), StateOf(i), Opt(i, "SettledDate") is { } d && DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var paid) ? paid : null)).ToList());
    }

    public async Task<ErpQuery<ErpPayment>> GetVendorPaymentsAsync(ErpContext context, DateTimeOffset since, CancellationToken ct = default)
    {
        var filter = $"$filter=dataAreaId eq '{Q(context.Company.Company)}' and TransactionDate ge {since.UtcDateTime:yyyy-MM-dd}";
        var page = await ListAllAsync(context, Set(context, "vendorPayments"), filter, ct);
        if (page.Failure is not null) return Fail<ErpPayment>(page.Failure);
        return ErpQuery<ErpPayment>.Of(page.Items.Select(p => new ErpPayment(
            Str(p, "JournalBatchNumber") + "/" + Str(p, "LineNumber"), Str(p, "Account"), Opt(p, "Invoice"), Dec(p, "DebitAmount"), Str(p, "CurrencyCode"),
            new DateTimeOffset(Date(p, "TransactionDate").ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))).ToList());
    }

    // --- purchase orders and dimensions ------------------------------------------------------------------------

    public async Task<ErpQuery<ErpPurchaseOrder>> ListPurchaseOrdersAsync(ErpContext context, DateTimeOffset? changedSince, CancellationToken ct = default)
    {
        var filter = $"$filter=dataAreaId eq '{Q(context.Company.Company)}'";
        if (changedSince is { } since) filter += $" and ModifiedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        var headers = await ListAllAsync(context, Set(context, "purchaseOrders"), filter, ct);
        if (headers.Failure is not null) return Fail<ErpPurchaseOrder>(headers.Failure);
        var lines = await ListAllAsync(context, Set(context, "purchaseOrderLines"), $"$filter=dataAreaId eq '{Q(context.Company.Company)}'", ct);
        if (lines.Failure is not null) return Fail<ErpPurchaseOrder>(lines.Failure);

        var byOrder = lines.Items.GroupBy(l => Str(l, "PurchaseOrderNumber")).ToDictionary(g => g.Key, g => g.ToList());
        var orders = headers.Items.Select(h =>
        {
            var number = Str(h, "PurchaseOrderNumber");
            var ls = byOrder.GetValueOrDefault(number) ?? [];
            return new ErpPurchaseOrder(
                number, 1, Str(h, "OrderVendorAccountNumber"), Str(h, "CurrencyCode"), Str(h, "PurchaseOrderStatus"), Date(h, "OrderCreationDate"),
                ls.Sum(l => Dec(l, "ReceivedPurchaseQuantity") * Dec(l, "PurchasePrice")),
                ls.Select(l => new ErpPurchaseOrderLine(Str(l, "LineNumber"), Str(l, "LineDescription"), Dec(l, "OrderedPurchaseQuantity"), Dec(l, "PurchasePrice"), Dec(l, "ReceivedPurchaseQuantity"))).ToList());
        }).ToList();
        return ErpQuery<ErpPurchaseOrder>.Of(orders);
    }

    public async Task<ErpQuery<ErpDimensionValue>> GetDimensionValuesAsync(ErpContext context, string dimension, CancellationToken ct = default)
    {
        var page = await ListAllAsync(context, Set(context, "dimensionValues"), $"$filter=DimensionName eq '{Q(dimension)}'", ct);
        if (page.Failure is not null) return Fail<ErpDimensionValue>(page.Failure);
        return ErpQuery<ErpDimensionValue>.Of(page.Items.Select(v => new ErpDimensionValue(dimension, Str(v, "DimensionValue"), Opt(v, "Description"))).ToList());
    }

    // --- HTTP ------------------------------------------------------------------------------------------------

    private sealed record Page(IReadOnlyList<JsonObject> Items, ErpResult? Failure);

    private sealed record Reply(JsonObject? Body, IReadOnlyList<JsonObject> Items, string? NextLink, ErpResult? Failure);

    private async Task<Page> ListAllAsync(ErpContext context, string set, string query, CancellationToken ct)
    {
        var items = new List<JsonObject>();
        var first = $"$top={context.Connection.PageSize}" + (string.IsNullOrEmpty(query) ? "" : "&" + query);
        string? next = $"{set}?{first}";
        for (var guard = 0; next is not null && guard < 1000; guard++)
        {
            var reply = await RequestAsync(context, HttpMethod.Get, next, null, ct);
            if (reply.Failure is not null) return new Page([], reply.Failure);
            items.AddRange(reply.Items);
            next = reply.NextLink;
        }

        return new Page(items, null);
    }

    private async Task<Reply> GetAsync(ErpContext context, string set, string query, CancellationToken ct) =>
        await RequestAsync(context, HttpMethod.Get, $"{set}?{query}", null, ct);

    private Task<Reply> SendAsync(ErpContext context, HttpMethod method, string path, JsonObject body, CancellationToken ct) => RequestAsync(context, method, path, body, ct);

    private async Task<Reply> RequestAsync(ErpContext context, HttpMethod method, string pathOrUrl, JsonObject? body, CancellationToken ct)
    {
        var token = await TokenAsync(context.Connection, ct);
        if (token.Failure is not null) return new Reply(null, [], null, token.Failure);

        var url = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) ? absolute : new Uri($"{context.Connection.BaseUrl.TrimEnd('/')}/data/{pathOrUrl}");
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (method == HttpMethod.Patch) request.Headers.Add("If-Match", "*");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(context.Connection.TimeoutSeconds));
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Reply(null, [], null, ErpResult.Transient("timeout", "D365 did not answer in time."));
        }
        catch (HttpRequestException ex)
        {
            return new Reply(null, [], null, ErpResult.Transient("network_error", ex.Message));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return new Reply(null, [], null, await ClassifyAsync(response, ct));
            var text = response.StatusCode == HttpStatusCode.NoContent ? "" : await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return new Reply(null, [], null, null);
            try
            {
                var node = JsonNode.Parse(text) as JsonObject;
                if (node?["value"] is JsonArray array)
                {
                    return new Reply(null, array.OfType<JsonObject>().ToList(), node["@odata.nextLink"]?.GetValue<string>(), null);
                }

                return new Reply(node, [], null, null);
            }
            catch (JsonException)
            {
                return new Reply(null, [], null, ErpResult.Rejected("bad_response", "D365 returned something that is not JSON."));
            }
        }
    }

    private static async Task<ErpResult> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var message = await ErrorMessageAsync(response, ct);
        var retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        if (retryAfter is { } r && r < TimeSpan.Zero) retryAfter = null;

        return status switch
        {
            429 => ErpResult.Transient("throttled", message, retryAfter ?? TimeSpan.FromSeconds(60)),
            408 or 500 or 502 or 503 or 504 => ErpResult.Transient($"erp_http_{status}", message, retryAfter),
            401 or 403 => ErpResult.Transient("auth_failed", message),
            _ => ErpResult.Rejected($"erp_http_{status}", message),
        };
    }

    private static async Task<string?> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(text);
            var message = node?["error"]?["message"]?.GetValue<string>() ?? node?["error"]?.ToString();
            return message is { Length: > 400 } ? message[..400] : message;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record TokenResult(string? Value, ErpResult? Failure);

    private async Task<TokenResult> TokenAsync(ErpConnectionSettings c, CancellationToken ct)
    {
        var cacheKey = $"{c.AuthorityTenantId}|{c.ClientId}|{c.BaseUrl}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.ClientSecret)))[..12]}";
        lock (_tokenGate)
        {
            if (_tokens.TryGetValue(cacheKey, out var cached) && cached.Expires > clock.GetUtcNow().AddMinutes(2)) return new TokenResult(cached.Token, null);
        }

        var authority = options.Value.Authority.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{authority}/{Uri.EscapeDataString(c.AuthorityTenantId)}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = c.ClientId,
                ["client_secret"] = c.ClientSecret,
                ["scope"] = c.BaseUrl.TrimEnd('/') + "/.default",
            }),
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(c.TimeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var failure = (int)response.StatusCode is 429 or >= 500
                    ? ErpResult.Transient("token_unavailable", "The sign-in service is not available.")
                    : ErpResult.Rejected("auth_failed", "D365 sign-in was refused. Check the client id and the secret in the vault.");
                return new TokenResult(null, failure);
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var token = json?["access_token"]?.GetValue<string>();
            var seconds = json?["expires_in"]?.GetValue<int>() ?? 3000;
            if (string.IsNullOrEmpty(token)) return new TokenResult(null, ErpResult.Rejected("auth_failed", "The sign-in service returned no token."));
            lock (_tokenGate) _tokens[cacheKey] = (token, clock.GetUtcNow().AddSeconds(seconds));
            return new TokenResult(token, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TokenResult(null, ErpResult.Transient("timeout", "The sign-in service did not answer in time."));
        }
        catch (HttpRequestException ex)
        {
            return new TokenResult(null, ErpResult.Transient("network_error", ex.Message));
        }
    }

    // --- helpers ---------------------------------------------------------------------------------------------

    private static string Set(ErpContext c, string role) => c.Connection.EntitySets.TryGetValue(role, out var name) && !string.IsNullOrWhiteSpace(name) ? name : D365Defaults.EntitySets[role];

    /// <summary>A short, stable reference derived from the idempotency key. D365 text fields are short, and the key itself is long.</summary>
    public static string Reference(string idempotencyKey) => "VPMS:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))[..32].ToLowerInvariant();

    private static string Q(string value) => value.Replace("'", "''");

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Str(JsonObject o, string name) => o[name]?.ToString() ?? "";

    private static string? Opt(JsonObject o, string name) => o[name] is { } n && !string.IsNullOrWhiteSpace(n.ToString()) ? n.ToString() : null;

    private static decimal Dec(JsonObject o, string name) => o[name] is { } n && decimal.TryParse(n.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static DateOnly Date(JsonObject o, string name) =>
        o[name] is { } n && DateTimeOffset.TryParse(n.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? DateOnly.FromDateTime(d.UtcDateTime) : default;

    private static string StateOf(JsonObject i) => Str(i, "InvoiceStatus") switch
    {
        "Paid" or "Settled" => ErpDocumentState.Paid,
        "Cancelled" or "Voided" => ErpDocumentState.Cancelled,
        "Open" or "Pending" or "Draft" => ErpDocumentState.Open,
        _ => ErpDocumentState.Posted,
    };

    private static ErpQuery<T> Fail<T>(ErpResult failure) => ErpQuery<T>.Failed(failure.Outcome, failure.ErrorCode ?? "erp_error", failure.RetryAfter);
}
