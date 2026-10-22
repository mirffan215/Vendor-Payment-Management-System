using Vpms.ErpIntegration.Domain;

namespace Vpms.UnitTests.ErpIntegration;

public sealed class ErpConfigValidationTests
{
    private static ConnectionConfig Valid() => new(ErpProviders.D365Finance, "https://contoso.operations.dynamics.com", "tenant", "client", "erp-client-secret", new Dictionary<string, string>());

    private static MappingConfig ValidMapping() => new("USMF", "VENDINV", null, new Dictionary<string, string>(), new Dictionary<string, string>(), []);

    [Fact]
    public void A_valid_connection_passes() => Assert.Empty(Valid().Validate());

    [Theory]
    [InlineData("http://contoso.example.com")]
    [InlineData("not a url")]
    [InlineData("ftp://x.example.com")]
    public void The_base_url_must_be_https(string url) => Assert.Contains((Valid() with { BaseUrl = url }).Validate(), i => i.Code == "base_url_invalid");

    [Fact]
    public void Plain_http_is_allowed_only_for_loopback()
    {
        Assert.Empty((Valid() with { BaseUrl = "http://localhost:5099" }).Validate());
        Assert.Contains((Valid() with { BaseUrl = "http://erp.example.com" }).Validate(), i => i.Code == "base_url_invalid");
    }

    [Theory]
    [InlineData("a secret with spaces")]
    [InlineData("")]
    [InlineData("-leading")]
    public void Only_a_vault_reference_is_accepted_not_free_text(string reference) =>
        Assert.Contains((Valid() with { SecretRef = reference }).Validate(), i => i.Code == "secret_ref_invalid");

    [Fact]
    public void An_unknown_provider_and_entity_set_role_are_refused()
    {
        Assert.Contains((Valid() with { Provider = "SAP" }).Validate(), i => i.Code == "provider_unknown");
        Assert.Contains((Valid() with { EntitySets = new Dictionary<string, string> { ["bogus"] = "X" } }).Validate(), i => i.Code == "entity_set_role_unknown");
        Assert.Contains((Valid() with { EntitySets = new Dictionary<string, string> { ["vendors"] = "Bad Name;DROP" } }).Validate(), i => i.Code == "entity_set_name_invalid");
    }

    [Fact]
    public void D365_needs_directory_and_client_ids_and_limits_are_bounded()
    {
        Assert.Contains((Valid() with { AuthorityTenantId = "" }).Validate(), i => i.Code == "authority_required");
        Assert.Contains((Valid() with { ClientId = "" }).Validate(), i => i.Code == "client_id_required");
        Assert.Contains((Valid() with { PageSize = 5 }).Validate(), i => i.Code == "page_size_invalid");
        Assert.Contains((Valid() with { TimeoutSeconds = 1 }).Validate(), i => i.Code == "timeout_invalid");
    }

    [Fact]
    public void The_connection_round_trips_through_json_without_any_secret_field()
    {
        var json = Valid().ToJson();
        Assert.DoesNotContain("clientSecret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Valid().SecretRef, ConnectionConfig.Parse(json).SecretRef);
    }

    [Fact]
    public void A_valid_mapping_passes() => Assert.Empty(ValidMapping().Validate());

    [Fact]
    public void Mapping_rejects_missing_company_duplicate_dimensions_and_bad_values()
    {
        Assert.Contains((ValidMapping() with { Company = "" }).Validate(), i => i.Code == "company_invalid");
        Assert.Contains((ValidMapping() with { JournalName = "bad;name" }).Validate(), i => i.Code == "journal_invalid");
        var dup = new DimensionMap("CostCenter", "CostCenter", null, new Dictionary<string, string>());
        Assert.Contains((ValidMapping() with { Dimensions = [dup, dup] }).Validate(), i => i.Code == "dimension_duplicate");
        Assert.Contains((ValidMapping() with { TaxCodeMap = new Dictionary<string, string> { ["S"] = "bad;value" } }).Validate(), i => i.Code == "map_value_invalid");
    }

    [Fact]
    public void A_change_cannot_be_approved_by_its_maker_and_only_pending_changes_can_be_decided()
    {
        var maker = Guid.NewGuid();
        var row = new ErpConfig { CreatedBy = maker, Status = ConfigStatus.Pending };
        Assert.Throws<Vpms.BuildingBlocks.Security.SegregationOfDutiesException>(() => row.Approve(maker, null, DateTimeOffset.UtcNow));
        row.Approve(Guid.NewGuid(), "ok", DateTimeOffset.UtcNow);
        Assert.Equal(ConfigStatus.Active, row.Status);
        Assert.Throws<Vpms.BuildingBlocks.Errors.ConflictException>(() => row.Approve(Guid.NewGuid(), null, DateTimeOffset.UtcNow));
    }
}
