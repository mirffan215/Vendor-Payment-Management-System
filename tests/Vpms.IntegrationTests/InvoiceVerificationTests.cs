using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Domain.Verification;
using Vpms.Invoices.Infrastructure;
using Vpms.TestSupport;
using Vpms.Vendors.Application;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class InvoiceVerificationTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private readonly FakePermissions _perms = new();
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _clerk = Guid.NewGuid();
    private readonly Guid _reviewer = Guid.NewGuid();
    private readonly Guid _overrider = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _maker = Guid.NewGuid();
    private readonly Guid _checker = Guid.NewGuid();
    private Guid _entity, _entity2, _vendor;

    public InvoiceVerificationTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(await _sql.CreateDatabaseAsync(), configure: s =>
        {
            s.AddSingleton<IPermissionEvaluator>(_perms);
            s.AddSingleton<IUserDirectory>(new FakeUsers(_reviewer, _clerk));
        });
        foreach (var t in new[] { _tenantA, _tenantB })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var admin = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            admin.Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await admin.SaveChangesAsync();
        }

        _entity = await ActiveVendorKit.CreateEntityAsync(_host.Services, _tenantA, _maker, "KSA");
        _entity2 = await ActiveVendorKit.CreateEntityAsync(_host.Services, _tenantA, _maker, "KSB");
        _vendor = await ActiveVendorKit.ActiveVendorAsync(_host.Services, _tenantA, _entity, "Al Amal Trading", _maker, _checker);
        await ActiveVendorKit.LinkAsync(_host.Services, _tenantA, _maker, _vendor, _entity2);

        _perms.Grant(_clerk, Permissions.InvoiceView, Permissions.InvoiceCreate, Permissions.InvoiceEdit);
        _perms.Grant(_reviewer, Permissions.InvoiceExceptionResolve);
        _perms.Grant(_overrider, Permissions.InvoiceExceptionOverride, Permissions.InvoiceExceptionResolve);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // --- helpers -------------------------------------------------------------------------------------------

    private Task<T> As<T>(Guid actor, Func<IServiceProvider, Task<T>> action, Guid? tenant = null) => ActiveVendorKit.AsAsync(_host.Services, tenant ?? _tenantA, actor, action);

    private static InvoiceData Data(string number = "INV-100", decimal net = 100m, int daysAgo = 10, string? po = null, IReadOnlyList<InvoiceLineInput>? lines = null, decimal? tax = null, decimal? total = null)
    {
        var t = tax ?? Math.Round(net * 0.15m, 2);
        return new InvoiceData(number, TestDates.Ago(daysAgo), TestDates.Ago(daysAgo).AddDays(30), "SAR", net, t, total ?? net + t, po, null,
            lines ?? [new InvoiceLineInput("Consulting", 1, net, net, "S", 0.15m, Math.Round(net * 0.15m, 2), null, null)]);
    }

    private async Task<InvoiceDto> Submit(InvoiceData data, Guid? entity = null, Guid? vendor = null)
    {
        var created = await As(_clerk, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(entity ?? _entity, vendor ?? _vendor, data)));
        return await As(_clerk, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id));
    }

    private Task<VerificationDetailDto> Detail(Guid invoiceId, Guid? tenant = null) =>
        As(_clerk, s => s.GetRequiredService<IInvoiceExceptionService>().DetailAsync(invoiceId), tenant);

    private static ExceptionDto Open(VerificationDetailDto d, string rule) => d.Exceptions.Single(e => e.RuleCode == rule && e.Status == ExceptionStatus.Open);

    private async Task Loosen(Guid entity, string rule, string severity)
    {
        var rules = new Dictionary<string, RuleSetting>(RuleConfig.Defaults().Rules) { [rule] = new(true, severity, RuleConfig.Defaults().Get(rule).Parameters) };
        var change = await As(_admin, s => s.GetRequiredService<IVerificationRuleSetService>().ProposeAsync(entity, new ProposeRuleChange(rules, "test")));
        if (change.Status == RuleSetStatus.Pending) await As(_approver, s => s.GetRequiredService<IVerificationRuleSetService>().ApproveAsync(change.Id!.Value, null));
    }

    private Task<int> ImportPo(string number, int version = 1, string status = "Open", decimal ordered = 10m, decimal price = 100m, Guid? entity = null, string currency = "SAR", decimal prevValue = 0m, decimal prevQty = 0m) =>
        As(_clerk, async s =>
        {
            var r = await s.GetRequiredService<IPurchaseOrderService>().ImportAsync(entity ?? _entity,
                [new PoImportItem(number, version, _vendor, null, currency, status, TestDates.Ago(30), prevValue, [new PoLineInput("1", "Consulting", ordered, price, prevQty)])]);
            Assert.Empty(r.Errors);
            return r.Created + r.Updated;
        });

    private static InvoiceLineInput PoLine(decimal qty, decimal price, string poLine = "1") =>
        new("Consulting", qty, price, qty * price, "S", 0.15m, Math.Round(qty * price * 0.15m, 2), poLine, null);

    private Task<InvoiceDto> SubmitPo(string number, decimal qty, decimal price, string po = "PO-1") =>
        Submit(Data(number, qty * price, po: po, lines: [PoLine(qty, price)]));

    // --- the pipeline ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_clean_invoice_is_verified_and_every_rule_is_recorded()
    {
        var invoice = await Submit(Data());
        Assert.Equal(InvoiceStatus.Verified, invoice.Status);

        var detail = await Detail(invoice.Id);
        Assert.Equal(RuleCatalog.All.Count, detail.Results.Count);
        Assert.Empty(detail.Exceptions);
        Assert.DoesNotContain(detail.Results, r => r.Outcome == Outcome.Failed);
        Assert.Equal(Outcome.Skipped, detail.Results.Single(r => r.RuleCode == RuleCodes.PoRequired).Outcome);
        Assert.Equal(0, detail.RuleSetVersion);
        Assert.Equal(1, detail.InvoiceVersion);
    }

    [Fact]
    public async Task Verification_is_audited()
    {
        var invoice = await Submit(Data());
        await using var scope = _host.Scope(_tenantA);
        Assert.True(await scope.ServiceProvider.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AnyAsync(e => e.Action == "invoice.verified" && e.EntityId == invoice.Id.ToString()));
    }

    [Fact]
    public async Task Verifying_twice_changes_nothing_and_stores_one_set_of_results()
    {
        var invoice = await Submit(Data());
        var again = await As(_clerk, s => s.GetRequiredService<IInvoiceVerifier>().VerifyAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Verified, again.InvoiceStatus);
        await using var scope = _host.Scope(_tenantA);
        Assert.Equal(RuleCatalog.All.Count, await scope.ServiceProvider.GetRequiredService<InvoicesDbContext>().VerificationResults.CountAsync(r => r.InvoiceId == invoice.Id));
    }

    [Fact]
    public async Task Two_verifications_at_the_same_moment_produce_one_result()
    {
        var created = await As(_clerk, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(_entity, _vendor, Data("RACE-1"))));
        await As(_clerk, async s =>
        {
            var db = s.GetRequiredService<InvoicesDbContext>();
            await db.Invoices.Where(i => i.Id == created.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, InvoiceStatus.Received).SetProperty(i => i.ReceivedAt, DateTimeOffset.UtcNow));
            return 0;
        });

        var results = await Task.WhenAll(
            As(_clerk, s => s.GetRequiredService<IInvoiceVerifier>().VerifyAsync(created.Id)),
            As(_clerk, s => s.GetRequiredService<IInvoiceVerifier>().VerifyAsync(created.Id)));
        Assert.All(results, r => Assert.Equal(InvoiceStatus.Verified, r.InvoiceStatus));
        await using var scope = _host.Scope(_tenantA);
        var runs = await scope.ServiceProvider.GetRequiredService<InvoicesDbContext>().VerificationResults.Where(r => r.InvoiceId == created.Id).Select(r => r.RunId).Distinct().CountAsync();
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task The_worker_sweep_verifies_invoices_left_waiting()
    {
        var created = await As(_clerk, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(_entity, _vendor, Data("STALE-1"))));
        await As(_clerk, async s =>
        {
            await s.GetRequiredService<InvoicesDbContext>().Invoices.Where(i => i.Id == created.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, InvoiceStatus.Received).SetProperty(i => i.ReceivedAt, DateTimeOffset.UtcNow.AddMinutes(-10)));
            return 0;
        });

        IReadOnlyList<Guid> tenants;
        await using (var scope = _host.Scope(null)) tenants = await scope.ServiceProvider.GetRequiredService<IVerificationScan>().TenantsWithUnverifiedInvoicesAsync(TimeSpan.FromMinutes(2));
        Assert.Equal([_tenantA], tenants);
        Assert.Equal(1, await As(_clerk, s => s.GetRequiredService<IVerificationScan>().VerifyStaleAsync(TimeSpan.FromMinutes(2))));
        Assert.Equal(InvoiceStatus.Verified, (await As(_clerk, s => s.GetRequiredService<IInvoiceService>().GetAsync(created.Id))).Status);
    }

    // --- duplicates -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_exact_duplicate_is_blocked_by_default()
    {
        var first = await Submit(Data("DUP-1"));
        var second = await Submit(Data("dup 1"));
        Assert.Equal(InvoiceStatus.Verified, first.Status);
        Assert.Equal(InvoiceStatus.Exception, second.Status);

        var exception = Open(await Detail(second.Id), RuleCodes.DuplicateExact);
        Assert.Equal(RuleSeverity.Block, exception.Severity);
        Assert.Equal(first.Id.ToString(), exception.Findings.Single().Evidence["invoiceId"]);
    }

    [Fact]
    public async Task The_same_number_with_a_different_amount_is_only_flagged()
    {
        await Submit(Data("DUP-2", 100m));
        var second = await Submit(Data("DUP-2", 250m));
        var detail = await Detail(second.Id);
        Assert.Equal(InvoiceStatus.Exception, second.Status);
        Assert.Equal(RuleSeverity.Flag, Open(detail, RuleCodes.DuplicateSameNumber).Severity);
        Assert.DoesNotContain(detail.Exceptions, e => e.RuleCode == RuleCodes.DuplicateExact);
    }

    [Fact]
    public async Task A_near_miss_is_flagged_as_similar()
    {
        await Submit(Data("INV-10010", 1000m, daysAgo: 10));
        var second = await Submit(Data("INV-10011", 1000m, daysAgo: 9));
        var exception = Open(await Detail(second.Id), RuleCodes.DuplicateFuzzy);
        Assert.Equal(RuleSeverity.Flag, exception.Severity);
        Assert.Equal("duplicate_similar", exception.Findings.Single().Key);
    }

    [Fact]
    public async Task Different_amounts_and_dates_with_unrelated_numbers_are_not_duplicates()
    {
        await Submit(Data("ALPHA-1", 100m, daysAgo: 10));
        var other = await Submit(Data("ZZZ-777", 480m, daysAgo: 60));
        Assert.Equal(InvoiceStatus.Verified, other.Status);
    }

    [Fact]
    public async Task The_same_invoice_in_another_legal_entity_is_not_a_duplicate_because_each_entity_keeps_its_own_records()
    {
        await Submit(Data("CROSS-1"));
        var other = await Submit(Data("CROSS-1"), entity: _entity2);
        Assert.Equal(InvoiceStatus.Verified, other.Status);
    }

    [Fact]
    public async Task Cancelled_rejected_and_draft_invoices_do_not_count()
    {
        var cancelled = await Submit(Data("GONE-1"));
        await As(_clerk, s => s.GetRequiredService<IInvoiceService>().CancelAsync(cancelled.Id, "wrong vendor"));
        await As(_clerk, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(_entity, _vendor, Data("GONE-1")))); // a draft with the same data

        var fresh = await Submit(Data("GONE-1"));
        Assert.Equal(InvoiceStatus.Verified, fresh.Status);
    }

    [Fact]
    public async Task The_same_data_gets_a_different_outcome_under_each_entitys_own_rules()
    {
        await Loosen(_entity2, RuleCodes.DuplicateExact, RuleSeverity.Flag);
        await Submit(Data("RULES-1"), entity: _entity);
        await Submit(Data("RULES-1"), entity: _entity2);
        var strict = await Submit(Data("RULES-1"), entity: _entity);
        var lax = await Submit(Data("RULES-1"), entity: _entity2);

        Assert.Equal(RuleSeverity.Block, Open(await Detail(strict.Id), RuleCodes.DuplicateExact).Severity);
        Assert.Equal(RuleSeverity.Flag, Open(await Detail(lax.Id), RuleCodes.DuplicateExact).Severity);
        Assert.Equal(1, (await Detail(lax.Id)).RuleSetVersion);
        Assert.Equal(0, (await Detail(strict.Id)).RuleSetVersion);
    }

    [Fact]
    public async Task A_disabled_duplicate_rule_lets_the_invoice_through_and_the_record_says_it_was_skipped()
    {
        var rules = new Dictionary<string, RuleSetting>(RuleConfig.Defaults().Rules)
        {
            [RuleCodes.DuplicateExact] = new(false, RuleSeverity.Block, new Dictionary<string, decimal>()),
            [RuleCodes.DuplicateSameNumber] = new(false, RuleSeverity.Flag, new Dictionary<string, decimal>()),
        };
        var change = await As(_admin, s => s.GetRequiredService<IVerificationRuleSetService>().ProposeAsync(_entity, new ProposeRuleChange(rules, "off")));
        await As(_approver, s => s.GetRequiredService<IVerificationRuleSetService>().ApproveAsync(change.Id!.Value, null));

        await Submit(Data("OFF-1"));
        var second = await Submit(Data("OFF-1"));
        Assert.Equal(InvoiceStatus.Verified, second.Status);
        Assert.Equal(Outcome.Skipped, (await Detail(second.Id)).Results.Single(r => r.RuleCode == RuleCodes.DuplicateExact).Outcome);
    }

    // --- purchase orders ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invoice_matching_its_order_passes()
    {
        await ImportPo("PO-1");
        Assert.Equal(InvoiceStatus.Verified, (await SubmitPo("PO-OK-1", 5, 100m)).Status);
    }

    [Fact]
    public async Task An_unknown_order_a_closed_order_and_another_currency_are_exceptions()
    {
        var unknown = await SubmitPo("PO-X-1", 1, 100m, po: "PO-MISSING");
        Assert.Equal("po_not_found", Open(await Detail(unknown.Id), RuleCodes.PoMatch).Findings.Single().Key);

        await ImportPo("PO-CLOSED", status: "Closed");
        var closed = await SubmitPo("PO-X-2", 1, 100m, po: "PO-CLOSED");
        Assert.Contains(Open(await Detail(closed.Id), RuleCodes.PoMatch).Findings, f => f.Key == "po_not_open");

        await ImportPo("PO-AED", currency: "AED");
        var aed = await SubmitPo("PO-X-3", 1, 100m, po: "PO-AED");
        Assert.Contains(Open(await Detail(aed.Id), RuleCodes.PoMatch).Findings, f => f.Key == "po_currency_mismatch");
    }

    [Fact]
    public async Task Price_differences_beyond_the_tolerance_are_found_and_within_it_pass()
    {
        await ImportPo("PO-1");
        Assert.Equal(InvoiceStatus.Verified, (await SubmitPo("PRICE-OK", 1, 102m)).Status);   // exactly 2% over
        var over = await SubmitPo("PRICE-BAD", 1, 103m);
        Assert.Equal(InvoiceStatus.Exception, over.Status);
        Assert.Equal("po_price_mismatch", Open(await Detail(over.Id), RuleCodes.PoMatch).Findings.Single().Key);
    }

    [Fact]
    public async Task Quantities_used_by_earlier_invoices_count_against_the_order()
    {
        await ImportPo("PO-1", ordered: 10m);
        Assert.Equal(InvoiceStatus.Verified, (await SubmitPo("QTY-1", 6, 100m)).Status);
        Assert.Equal(InvoiceStatus.Verified, (await SubmitPo("QTY-2", 4, 100m)).Status);   // 6 + 4 = 10
        var third = await SubmitPo("QTY-3", 1, 100m);                                        // 11 of 10
        var findings = Open(await Detail(third.Id), RuleCodes.PoMatch).Findings;
        Assert.Contains(findings, f => f.Key == "po_quantity_exceeded");
        Assert.Contains(findings, f => f.Key == "po_value_exceeded");
    }

    [Fact]
    public async Task Cancelling_an_invoice_gives_its_quantity_back_to_the_order()
    {
        await ImportPo("PO-1", ordered: 10m);
        var first = await SubmitPo("BACK-1", 10, 100m);
        await As(_clerk, s => s.GetRequiredService<IInvoiceService>().CancelAsync(first.Id, "entered twice"));
        Assert.Equal(InvoiceStatus.Verified, (await SubmitPo("BACK-2", 10, 100m)).Status);
    }

    [Fact]
    public async Task History_imported_from_the_erp_counts_too()
    {
        await ImportPo("PO-1", ordered: 10m, prevQty: 9m, prevValue: 900m);
        var invoice = await SubmitPo("HIST-1", 2, 100m);
        Assert.Contains(Open(await Detail(invoice.Id), RuleCodes.PoMatch).Findings, f => f.Key == "po_quantity_exceeded");
    }

    [Fact]
    public async Task Requiring_a_po_is_a_per_entity_choice()
    {
        var rules = new Dictionary<string, RuleSetting>(RuleConfig.Defaults().Rules) { [RuleCodes.PoRequired] = new(true, RuleSeverity.Block, new Dictionary<string, decimal>()) };
        await As(_admin, s => s.GetRequiredService<IVerificationRuleSetService>().ProposeAsync(_entity, new ProposeRuleChange(rules, "POs mandatory")));

        var without = await Submit(Data("NOPO-1"));
        Assert.Equal(RuleSeverity.Block, Open(await Detail(without.Id), RuleCodes.PoRequired).Severity);
        Assert.Equal(InvoiceStatus.Verified, (await Submit(Data("NOPO-1"), entity: _entity2)).Status); // the other entity does not require it
    }

    [Fact]
    public async Task Another_tenants_purchase_orders_are_invisible_to_matching()
    {
        await ImportPo("PO-1");
        Assert.Empty(await As(_clerk, s => s.GetRequiredService<IPurchaseOrderService>().ListAsync(null, null, null, 0, 50), _tenantB));
        Assert.Single(await As(_clerk, s => s.GetRequiredService<IPurchaseOrderService>().ListAsync(null, "PO-1", null, 0, 50)));
    }

    // --- amounts, dates and vendor --------------------------------------------------------------------------

    [Fact]
    public async Task Totals_that_do_not_add_up_block_the_invoice_and_correcting_them_clears_the_exception()
    {
        var bad = await Submit(Data("MATH-1", total: 130m));
        Assert.Equal(InvoiceStatus.Exception, bad.Status);
        var exception = Open(await Detail(bad.Id), RuleCodes.AmountTotals);
        Assert.Equal(RuleSeverity.Block, exception.Severity);

        var fixedUp = await As(_clerk, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(bad.Id, Data("MATH-1")));
        Assert.Equal(InvoiceStatus.Verified, fixedUp.Status);
        Assert.Equal(2, fixedUp.Version);
        var detail = await Detail(bad.Id);
        Assert.Equal(ExceptionStatus.Resolved, detail.Exceptions.Single().Status);
        Assert.Equal("corrected", detail.Exceptions.Single().Resolution);
    }

    [Fact]
    public async Task Old_and_future_dated_invoices_are_flagged()
    {
        Assert.Equal("invoice_too_old", Open(await Detail((await Submit(Data("OLD-1", daysAgo: 400))).Id), RuleCodes.DateSanity).Findings.Single().Key);
        Assert.Equal("invoice_date_in_future", Open(await Detail((await Submit(Data("FUT-1", daysAgo: -3))).Id), RuleCodes.DateSanity).Findings.Single().Key);
    }

    [Fact]
    public async Task A_vendor_that_stops_being_active_before_verification_is_blocked_and_cannot_be_waived()
    {
        var created = await As(_clerk, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(_entity, _vendor, Data("SUSP-1"))));
        await As(_maker, s => s.GetRequiredService<IVendorService>().SuspendAsync(_vendor, "under investigation"));
        var submitted = await As(_clerk, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id));
        Assert.Equal(InvoiceStatus.Exception, submitted.Status);

        var exception = Open(await Detail(created.Id), RuleCodes.VendorState);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => As(_overrider, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, "please")));
        Assert.Equal("not_waivable", ex.Code);
    }

    // --- the exceptions queue -------------------------------------------------------------------------------

    [Fact]
    public async Task A_flag_is_waived_with_a_reason_and_the_invoice_moves_on()
    {
        await Submit(Data("W-1", 100m));
        var flagged = await Submit(Data("W-1", 250m));
        var exception = Open(await Detail(flagged.Id), RuleCodes.DuplicateSameNumber);

        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, " ")))).Code);
        var waived = await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, "Corrected invoice, original was cancelled at the vendor"));
        Assert.Equal(ExceptionStatus.Waived, waived.Status);
        Assert.Equal(_reviewer, waived.ResolvedBy);
        Assert.Equal(InvoiceStatus.Verified, (await As(_clerk, s => s.GetRequiredService<IInvoiceService>().GetAsync(flagged.Id))).Status);

        await Assert.ThrowsAsync<ConflictException>(() => As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, "again")));
        await using var scope = _host.Scope(_tenantA);
        Assert.True(await scope.ServiceProvider.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AnyAsync(e => e.Action == "invoice.exception.waived" && e.EntityId == exception.Id.ToString()));
    }

    [Fact]
    public async Task Waiving_a_block_needs_the_override_permission_and_a_person_who_did_not_make_the_invoice()
    {
        await Submit(Data("B-1"));
        var blocked = await Submit(Data("B-1"));
        var exception = Open(await Detail(blocked.Id), RuleCodes.DuplicateExact);

        // a reviewer with only the resolve right cannot
        Assert.Equal("override_required", (await Assert.ThrowsAsync<ConflictException>(() => As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, "ok")))).Code);

        // the maker of the invoice cannot, even holding the override right
        _perms.Grant(_clerk, Permissions.InvoiceExceptionOverride);
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_clerk, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, "ok")));
        await using (var scope = _host.Scope(_tenantA))
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AnyAsync(e => e.Action == "sod.invoice-exception.blocked" && e.EntityId == exception.Id.ToString()));
        }

        // a different person with the override right can
        var waived = await As(_overrider, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, "Vendor resent after a bank rejection; original is void"));
        Assert.Equal(ExceptionStatus.Waived, waived.Status);
        Assert.Equal(InvoiceStatus.Verified, (await As(_clerk, s => s.GetRequiredService<IInvoiceService>().GetAsync(blocked.Id))).Status);
    }

    [Fact]
    public async Task The_last_person_to_edit_the_invoice_cannot_waive_its_block_either()
    {
        await Submit(Data("B-2"));
        var blocked = await Submit(Data("B-2"));
        // another clerk edits it without fixing the duplicate, then tries to approve their own exception
        var editor = Guid.NewGuid();
        _perms.Grant(editor, Permissions.InvoiceEdit, Permissions.InvoiceExceptionOverride);
        await As(editor, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(blocked.Id, Data("B-2", daysAgo: 10)));
        var exception = Open(await Detail(blocked.Id), RuleCodes.DuplicateExact);
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(editor, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, "mine")));
    }

    [Fact]
    public async Task A_waiver_applies_only_to_the_version_it_was_given_for()
    {
        await Submit(Data("V-3", 100m));
        var bad = await Submit(Data("V-3", 250m, total: 300m)); // totals do not add up (block) and the number was used before (flag)
        var detail = await Detail(bad.Id);
        await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(Open(detail, RuleCodes.DuplicateSameNumber).Id, "Corrected invoice"));
        Assert.Equal(InvoiceStatus.Exception, (await As(_clerk, s => s.GetRequiredService<IInvoiceService>().GetAsync(bad.Id))).Status); // the block is still open

        // fixing the totals makes version 2; the waiver was for version 1, so the repeated number is raised again
        var edited = await As(_clerk, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(bad.Id, Data("V-3", 250m)));
        Assert.Equal(2, edited.Version);
        Assert.Equal(InvoiceStatus.Exception, edited.Status);
        var again = Open(await Detail(bad.Id), RuleCodes.DuplicateSameNumber);
        Assert.Equal(2, again.InvoiceVersion);

        // waived for version 2, then verified again without any edit: the waiver stands
        await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(again.Id, "Still the corrected invoice"));
        var reverified = await As(_clerk, s => s.GetRequiredService<IInvoiceExceptionService>().ReverifyAsync(bad.Id));
        Assert.Equal(InvoiceStatus.Verified, reverified.InvoiceStatus);
        Assert.DoesNotContain(reverified.Exceptions, e => e.Status == ExceptionStatus.Open);
    }

    [Fact]
    public async Task Reverifying_after_a_purchase_order_arrives_resolves_the_exception()
    {
        var invoice = await SubmitPo("LATE-1", 2, 100m, po: "PO-LATE");
        Assert.Equal(InvoiceStatus.Exception, invoice.Status);

        await ImportPo("PO-LATE");
        var detail = await As(_clerk, s => s.GetRequiredService<IInvoiceExceptionService>().ReverifyAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Verified, detail.InvoiceStatus);
        Assert.Equal(ExceptionStatus.Resolved, detail.Exceptions.Single().Status);
        Assert.Equal(1, detail.InvoiceVersion); // the invoice itself did not change
    }

    [Fact]
    public async Task Exceptions_can_be_assigned_to_a_real_user_and_listed_by_assignee_status_and_scope()
    {
        await Submit(Data("A-1", 100m));
        var flagged = await Submit(Data("A-1", 250m));
        var exception = Open(await Detail(flagged.Id), RuleCodes.DuplicateSameNumber);

        Assert.Equal("unknown_user", (await Assert.ThrowsAsync<ValidationFailedException>(() => As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().AssignAsync(exception.Id, Guid.NewGuid())))).Code);
        var assigned = await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().AssignAsync(exception.Id, _reviewer));
        Assert.Equal(_reviewer, assigned.AssignedTo);
        Assert.Single(await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().ListAsync(ExceptionStatus.Open, _reviewer, null, 0, 50)));
        Assert.Empty(await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().ListAsync(ExceptionStatus.Open, _clerk, null, 0, 50)));
        Assert.Null((await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().AssignAsync(exception.Id, null))).AssignedTo);

        var open = await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().ListAsync(ExceptionStatus.Open, null, null, 0, 50));
        Assert.Contains(open, e => e.Id == exception.Id);
        Assert.Empty(await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().ListAsync(ExceptionStatus.Open, null, new HashSet<Guid> { _entity2 }, 0, 50)));
        Assert.Empty(await As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().ListAsync(ExceptionStatus.Open, null, null, 0, 50), _tenantB));
        await Assert.ThrowsAsync<NotFoundException>(() => As(_reviewer, s => s.GetRequiredService<IInvoiceExceptionService>().WaiveAsync(exception.Id, "x"), _tenantB));
    }

    // --- purchase-order import ------------------------------------------------------------------------------

    [Fact]
    public async Task Importing_is_idempotent_versioned_and_reports_problems_per_order()
    {
        PoImportItem Item(string number, int version, decimal qty = 10m, Guid? vendor = null, string? erp = null) =>
            new(number, version, vendor ?? (erp is null ? _vendor : null), erp, "SAR", "Open", TestDates.Ago(20), 0m, [new PoLineInput("1", "Item", qty, 100m, 0m)]);
        Task<PoImportResult> Import(params PoImportItem[] items) => As(_clerk, s => s.GetRequiredService<IPurchaseOrderService>().ImportAsync(_entity, items));

        static (int, int, int, int) Counts(PoImportResult r) => (r.Created, r.Updated, r.Unchanged, r.Errors.Count);
        Assert.Equal((1, 0, 0, 0), Counts(await Import(Item("IMP-1", 1))));
        Assert.Equal((0, 0, 1, 0), Counts(await Import(Item("IMP-1", 1))));           // replay: nothing changes
        Assert.Equal((0, 1, 0, 0), Counts(await Import(Item("IMP-1", 2, qty: 12m)))); // a newer version replaces it

        var mixed = await Import(Item("IMP-1", 1), Item("IMP-1", 2, qty: 99m), Item("IMP-2", 1, vendor: Guid.NewGuid()), Item("IMP-3", 1));
        Assert.Equal(1, mixed.Created);                                                          // IMP-3 still went in
        Assert.Equal(["po_older_version", "po_version_conflict", "unknown_vendor"], mixed.Errors.Select(e => e.Code).ToArray());

        var stored = await As(_clerk, s => s.GetRequiredService<IPurchaseOrderService>().ListAsync(_entity, "IMP-1", null, 0, 10));
        var po = Assert.Single(stored);
        Assert.Equal(2, po.Version);
        Assert.Equal(12m, Assert.Single(po.Lines).OrderedQuantity);
    }

    [Fact]
    public async Task Orders_can_name_the_vendor_by_its_erp_account_and_bad_input_is_refused()
    {
        await As(_maker, s => s.GetRequiredService<IVendorEntityLinkService>().SaveAsync(_vendor, _entity, new SaveVendorEntityLink("V-1001", null, "SAR", null, null, null)));
        var byAccount = await As(_clerk, s => s.GetRequiredService<IPurchaseOrderService>().ImportAsync(_entity,
            [new PoImportItem("ERP-1", 1, null, "V-1001", "SAR", "Open", TestDates.Ago(1), 0m, [new PoLineInput("1", "Item", 1, 10m, 0m)])]));
        Assert.Equal(1, byAccount.Created);

        PoImportItem Bad(Func<PoImportItem, PoImportItem> change) => change(new PoImportItem("BAD", 1, _vendor, null, "SAR", "Open", TestDates.Ago(1), 0m, [new PoLineInput("1", "Item", 1, 10m, 0m)]));
        var result = await As(_clerk, s => s.GetRequiredService<IPurchaseOrderService>().ImportAsync(_entity,
        [
            Bad(p => p with { CurrencyCode = "sar" }), Bad(p => p with { Status = "Maybe" }), Bad(p => p with { Version = 0 }), Bad(p => p with { Lines = [] }),
            Bad(p => p with { Lines = [new PoLineInput("1", "A", 1, 1, 0), new PoLineInput("1", "B", 1, 1, 0)] }), Bad(p => p with { PoNumber = " " }),
            Bad(p => p with { Lines = [new PoLineInput("1", "A", -1, 1, 0)] }),
        ]));
        Assert.Equal(7, result.Errors.Count);
        Assert.Equal(0, result.Created);

        await Assert.ThrowsAsync<ValidationFailedException>(() => As(_clerk, s => s.GetRequiredService<IPurchaseOrderService>().ImportAsync(Guid.NewGuid(), [])));
        await Assert.ThrowsAsync<ValidationFailedException>(() => As(_clerk, s => s.GetRequiredService<IPurchaseOrderService>().ImportAsync(_entity, [])));
    }

    [Fact]
    public async Task Replacing_an_order_keeps_matching_working_with_the_new_lines()
    {
        await ImportPo("PO-1", ordered: 10m);
        Assert.Equal(InvoiceStatus.Exception, (await SubmitPo("REPL-1", 11, 100m)).Status);
        await ImportPo("PO-1", version: 2, ordered: 30m);
        Assert.Equal(InvoiceStatus.Verified, (await SubmitPo("REPL-2", 11, 100m)).Status);
    }

    private sealed class FakeUsers(params Guid[] known) : IUserDirectory
    {
        public Task<bool> ExistsAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(known.Contains(userId));

        public Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IEnumerable<Guid> userIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, UserSummary>>(new Dictionary<Guid, UserSummary>());
    }

    // --- test double ----------------------------------------------------------------------------------------

    private sealed class FakePermissions : IPermissionEvaluator
    {
        private readonly Dictionary<Guid, HashSet<string>> _grants = [];

        public void Grant(Guid user, params string[] permissions)
        {
            if (!_grants.TryGetValue(user, out var set)) _grants[user] = set = [];
            foreach (var p in permissions) set.Add(p);
        }

        public Task<bool> HasAsync(Guid userId, string permission, Guid? legalEntityId = null, CancellationToken ct = default) =>
            Task.FromResult(_grants.TryGetValue(userId, out var set) && set.Contains(permission));

        public Task<PermissionScope> GetScopeAsync(Guid userId, string permission, CancellationToken ct = default) =>
            Task.FromResult(_grants.TryGetValue(userId, out var set) && set.Contains(permission) ? new PermissionScope(true, new HashSet<Guid>()) : PermissionScope.None);
    }
}
