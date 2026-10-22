using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Domain.Tax;
using Vpms.Invoices.Domain.Verification;
using Vpms.Tax.Sa;
using Vpms.TestSupport;
using Vpms.Vendors.Application;

namespace Vpms.IntegrationTests;

/// <summary>Country tax plug-ins chosen by the legal entity's country, and the entity's own switches for them.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class InvoiceTaxTests : IAsyncLifetime
{
    private const string VendorVat = "300012345600003";
    private readonly SqlServerFixture _sql;
    private IntegrationHost _withSaudi = null!;
    private IntegrationHost _withoutSaudi = null!;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _clerk = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _maker = Guid.NewGuid();
    private readonly Guid _checker = Guid.NewGuid();
    private Guid _saEntity, _aeEntity, _vendor;

    public InvoiceTaxTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        var db = await _sql.CreateDatabaseAsync();
        var kek = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _withSaudi = new IntegrationHost(db, configure: s =>
        {
            s.Configure<Vpms.BuildingBlocks.Security.Encryption.EncryptionOptions>(o => o.LocalKek = kek);
            s.AddSingleton<ITaxRuleProvider, SaudiVatProvider>();
        });
        // the same database and keys, with the Saudi plug-in not registered
        _withoutSaudi = new IntegrationHost(db, configure: s => s.Configure<Vpms.BuildingBlocks.Security.Encryption.EncryptionOptions>(o => o.LocalKek = kek));
        await using (var scope = _withSaudi.Scope(null))
        {
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var admin = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            admin.Tenants.Add(new Tenant { Id = _tenant, Name = "T", Slug = "t" + _tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await admin.SaveChangesAsync();
        }

        _saEntity = await ActiveVendorKit.CreateEntityAsync(_withSaudi.Services, _tenant, _maker, "KSA", "SA", "SAR");
        _aeEntity = await ActiveVendorKit.CreateEntityAsync(_withSaudi.Services, _tenant, _maker, "UAE", "AE", "AED");
        _vendor = await ActiveVendorKit.ActiveVendorAsync(_withSaudi.Services, _tenant, _saEntity, "Al Amal Trading", _maker, _checker);
        await ActiveVendorKit.LinkAsync(_withSaudi.Services, _tenant, _maker, _vendor, _aeEntity, "AED");
        await ActiveVendorKit.AsAsync(_withSaudi.Services, _tenant, _maker, s => s.GetRequiredService<IVendorService>()
            .UpdateAsync(_vendor, new UpdateVendor("Al Amal Trading", null, null, VendorVat, null, null, null)));
    }

    public async Task DisposeAsync()
    {
        await _withSaudi.DisposeAsync();
        await _withoutSaudi.DisposeAsync();
    }

    private static InvoiceData Data(string number, string? taxNumber, string currency = "SAR", string code = "S", decimal rate = 0.15m, string? reference = null) =>
        new(number, TestDates.Ago(5), null, currency, 100m, Math.Round(100m * rate, 2), 100m + Math.Round(100m * rate, 2), null, reference,
            [new InvoiceLineInput("Consulting", 1, 100m, 100m, code, rate, Math.Round(100m * rate, 2), null, null)], taxNumber);

    private async Task<InvoiceDto> Submit(IntegrationHost host, Guid entity, InvoiceData data, string currency = "SAR")
    {
        var created = await ActiveVendorKit.AsAsync(host.Services, _tenant, _clerk, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(entity, _vendor, data)));
        return await ActiveVendorKit.AsAsync(host.Services, _tenant, _clerk, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id));
    }

    private Task<VerificationDetailDto> Detail(IntegrationHost host, Guid invoiceId) =>
        ActiveVendorKit.AsAsync(host.Services, _tenant, _clerk, s => s.GetRequiredService<IInvoiceExceptionService>().DetailAsync(invoiceId));

    private static IReadOnlyList<string> TaxKeys(VerificationDetailDto d) =>
        d.Exceptions.Where(e => e.RuleCode == RuleCodes.TaxVat).SelectMany(e => e.Findings).Select(f => f.Key).ToList();

    [Fact]
    public async Task A_correct_saudi_invoice_passes_every_check()
    {
        var invoice = await Submit(_withSaudi, _saEntity, Data("SA-1", VendorVat, reference: Guid.NewGuid().ToString()));
        Assert.Equal(InvoiceStatus.Verified, invoice.Status);
        Assert.Equal(VendorVat, invoice.SupplierTaxNumber);
    }

    [Fact]
    public async Task A_malformed_supplier_vat_number_is_flagged_for_a_saudi_entity()
    {
        var invoice = await Submit(_withSaudi, _saEntity, Data("SA-2", "12345"));
        Assert.Equal(InvoiceStatus.Exception, invoice.Status);
        Assert.Contains("tax_number_invalid_format", TaxKeys(await Detail(_withSaudi, invoice.Id)));
        Assert.Equal(RuleSeverity.Flag, (await Detail(_withSaudi, invoice.Id)).Exceptions.Single().Severity);
    }

    [Fact]
    public async Task A_number_that_differs_from_the_vendor_master_is_flagged()
    {
        var invoice = await Submit(_withSaudi, _saEntity, Data("SA-3", "310122393500003"));
        Assert.Equal(["tax_number_vendor_mismatch"], TaxKeys(await Detail(_withSaudi, invoice.Id)));
    }

    [Fact]
    public async Task A_wrong_rate_for_the_category_is_flagged()
    {
        var invoice = await Submit(_withSaudi, _saEntity, Data("SA-4", VendorVat, code: "S", rate: 0.05m));
        Assert.Equal(["tax_rate_not_allowed"], TaxKeys(await Detail(_withSaudi, invoice.Id)));
    }

    [Fact]
    public async Task The_same_invoice_in_a_non_saudi_entity_gets_no_saudi_checks()
    {
        var invoice = await Submit(_withSaudi, _aeEntity, Data("AE-1", "12345", "AED", rate: 0.05m));
        var keys = TaxKeys(await Detail(_withSaudi, invoice.Id));
        Assert.DoesNotContain("tax_number_invalid_format", keys);   // no Saudi format rules
        Assert.DoesNotContain("tax_rate_not_allowed", keys);        // and no Saudi rates
        Assert.Equal(["tax_number_vendor_mismatch"], keys);         // the generic comparison with the vendor master still applies
        Assert.Equal(InvoiceStatus.Verified, (await Submit(_withSaudi, _aeEntity, Data("AE-1B", VendorVat, "AED", rate: 0.05m))).Status);
    }

    [Fact]
    public async Task Without_the_saudi_plug_registered_a_saudi_entity_falls_back_to_the_standard_checks()
    {
        var invoice = await Submit(_withoutSaudi, _saEntity, Data("SA-5", VendorVat + "0"));   // 16 digits: invalid for Saudi, but the standard fallback does not know
        Assert.Equal(["tax_number_vendor_mismatch"], TaxKeys(await Detail(_withoutSaudi, invoice.Id)));
        var clean = await Submit(_withoutSaudi, _saEntity, Data("SA-5B", VendorVat, rate: 0.05m));
        Assert.Equal(InvoiceStatus.Verified, clean.Status);
    }

    [Fact]
    public async Task Each_check_is_an_entity_switch_and_requiring_the_number_is_opt_in()
    {
        Assert.Equal(InvoiceStatus.Verified, (await Submit(_withSaudi, _saEntity, Data("SA-6", null))).Status); // not required by default

        var tax = RuleConfig.Defaults().Get(RuleCodes.TaxVat);
        var parameters = tax.Parameters.ToDictionary(p => p.Key, p => p.Value);
        parameters[TaxSettings.RequireSupplierTaxNumber] = 1m;          // tightening: takes effect at once
        var change = await ActiveVendorKit.AsAsync(_withSaudi.Services, _tenant, _admin, s => s.GetRequiredService<IVerificationRuleSetService>()
            .ProposeAsync(_saEntity, new ProposeRuleChange(new Dictionary<string, RuleSetting> { [RuleCodes.TaxVat] = new(true, RuleSeverity.Flag, parameters) }, "Input VAT claims need the number")));
        Assert.Equal(RuleSetStatus.Active, change.Status);

        var missing = await Submit(_withSaudi, _saEntity, Data("SA-7", null));
        Assert.Equal(["tax_number_required"], TaxKeys(await Detail(_withSaudi, missing.Id)));
        Assert.Equal(InvoiceStatus.Verified, (await Submit(_withSaudi, _aeEntity, Data("AE-2", null, "AED"))).Status); // the other entity is unaffected
    }

    [Fact]
    public async Task Switching_a_saudi_check_off_is_a_loosening_that_needs_approval()
    {
        var parameters = RuleConfig.Defaults().Get(RuleCodes.TaxVat).Parameters.ToDictionary(p => p.Key, p => p.Value);
        parameters[TaxSettings.CheckAllowedRates] = 0m;
        var change = await ActiveVendorKit.AsAsync(_withSaudi.Services, _tenant, _admin, s => s.GetRequiredService<IVerificationRuleSetService>()
            .ProposeAsync(_saEntity, new ProposeRuleChange(new Dictionary<string, RuleSetting> { [RuleCodes.TaxVat] = new(true, RuleSeverity.Flag, parameters) }, "Legacy rates")));
        Assert.Equal(RuleSetStatus.Pending, change.Status);
        Assert.Equal([RuleCodes.TaxVat], change.LoosenedRules);
    }

    [Fact]
    public async Task The_supplier_tax_number_is_stored_with_the_invoice_and_changes_with_edits()
    {
        var created = await ActiveVendorKit.AsAsync(_withSaudi.Services, _tenant, _clerk, s => s.GetRequiredService<IInvoiceService>()
            .CreateAsync(new CreateInvoice(_saEntity, _vendor, Data("SA-8", "  " + VendorVat + " "))));
        Assert.Equal(VendorVat, created.SupplierTaxNumber);
        var edited = await ActiveVendorKit.AsAsync(_withSaudi.Services, _tenant, _clerk, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(created.Id, Data("SA-8", null)));
        Assert.Null(edited.SupplierTaxNumber);
        await Assert.ThrowsAsync<Vpms.BuildingBlocks.Errors.ValidationFailedException>(() => ActiveVendorKit.AsAsync(_withSaudi.Services, _tenant, _clerk,
            s => s.GetRequiredService<IInvoiceService>().UpdateAsync(created.Id, Data("SA-8", new string('3', 31)))));
    }
}
