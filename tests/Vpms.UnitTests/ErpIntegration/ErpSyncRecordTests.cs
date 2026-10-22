using Vpms.BuildingBlocks.Errors;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.Invoices.Contracts;

namespace Vpms.UnitTests.ErpIntegration;

public sealed class ErpSyncRecordTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static ErpSyncRecord Record() => new() { Id = Guid.NewGuid(), NextAttemptAt = T0, CreatedAt = T0 };

    [Fact]
    public void Retry_delays_double_up_to_the_cap_and_jitter_only_adds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(120), RetryPolicy.Delay(3));
        Assert.Equal(RetryPolicy.Cap, RetryPolicy.Delay(30));
        Assert.True(RetryPolicy.Delay(3, 0.5) > RetryPolicy.Delay(3));
        Assert.True(RetryPolicy.Delay(3, 0.999) <= TimeSpan.FromSeconds(120 * 1.25));
    }

    [Fact]
    public void A_message_is_due_only_when_its_time_has_come_or_its_lease_has_expired()
    {
        var r = Record();
        Assert.True(r.IsDue(T0));
        Assert.False(r.IsDue(T0.AddSeconds(-1)));
        r.Lease("w1", T0);
        Assert.False(r.IsDue(T0.AddMinutes(1)));
        Assert.True(r.IsDue(T0.AddMinutes(ErpSyncRecord.LeaseMinutes)));
    }

    [Fact]
    public void A_leased_message_cannot_be_leased_again_until_the_lease_expires()
    {
        var r = Record();
        r.Lease("w1", T0);
        Assert.Equal(1, r.Attempts);
        Assert.Throws<ConflictException>(() => r.Lease("w2", T0.AddMinutes(1)));
        r.Lease("w2", T0.AddMinutes(6));
        Assert.Equal(2, r.Attempts);
        Assert.Equal("w2", r.LeaseOwner);
    }

    [Fact]
    public void A_transient_failure_waits_then_gives_up_after_the_last_attempt()
    {
        var r = Record();
        var now = T0;
        for (var i = 1; i < RetryPolicy.MaxAttempts; i++)
        {
            r.Lease("w", now);
            r.FailTransient("erp_unavailable", "down", now, null, 0);
            Assert.Equal(SyncStatus.Pending, r.Status);
            Assert.Equal(now + RetryPolicy.Delay(i), r.NextAttemptAt);
            now = r.NextAttemptAt;
        }

        r.Lease("w", now);
        r.FailTransient("erp_unavailable", "down", now, null, 0);
        Assert.Equal(SyncStatus.DeadLetter, r.Status);
        Assert.Equal("max_attempts", r.LastErrorCode);
        Assert.Contains("erp_unavailable", r.LastError);
    }

    [Fact]
    public void Retry_after_from_the_erp_is_honoured_but_capped()
    {
        var r = Record();
        r.Lease("w", T0);
        r.FailTransient("throttled", null, T0, TimeSpan.FromMinutes(10), 0);
        Assert.Equal(T0.AddMinutes(10), r.NextAttemptAt);
        r.Lease("w", r.NextAttemptAt);
        r.FailTransient("throttled", null, r.NextAttemptAt, TimeSpan.FromDays(3), 0);
        Assert.Equal(T0.AddMinutes(10) + RetryPolicy.Cap, r.NextAttemptAt);
    }

    [Fact]
    public void Deferring_does_not_use_up_an_attempt()
    {
        var r = Record();
        r.Lease("w", T0);
        r.Defer("vendor_not_synced", "wait", T0, TimeSpan.FromMinutes(2));
        Assert.Equal(0, r.Attempts);
        Assert.Equal(SyncStatus.Pending, r.Status);
        Assert.Equal(T0.AddMinutes(2), r.NextAttemptAt);
    }

    [Fact]
    public void Only_a_dead_letter_can_be_retried_or_resolved_and_both_need_a_reason()
    {
        var r = Record();
        Assert.Throws<ConflictException>(() => r.Requeue(Guid.NewGuid(), "why", T0));
        r.Lease("w", T0);
        r.DeadLetterNow("rejected", "no", T0);
        Assert.Throws<ValidationFailedException>(() => r.Requeue(Guid.NewGuid(), " ", T0));
        Assert.Throws<ValidationFailedException>(() => r.ResolveManually(Guid.NewGuid(), "", null, T0));

        var op = Guid.NewGuid();
        r.Requeue(op, "Fixed the mapping", T0.AddHours(1));
        Assert.Equal(SyncStatus.Pending, r.Status);
        Assert.Equal(0, r.Attempts);
        Assert.Equal(op, r.HandledBy);

        r.Lease("w", T0.AddHours(1));
        r.DeadLetterNow("rejected", "no", T0.AddHours(1));
        r.ResolveManually(op, "Posted by hand", " ERP-77 ", T0.AddHours(2));
        Assert.Equal(SyncStatus.Resolved, r.Status);
        Assert.Equal("ERP-77", r.ErpReference);
        Assert.Throws<ConflictException>(() => r.Requeue(op, "again", T0));
    }

    [Fact]
    public void A_finished_message_cannot_be_succeeded_or_failed_again()
    {
        var r = Record();
        r.Lease("w", T0);
        r.Succeed("ERP-1", T0);
        Assert.Throws<ConflictException>(() => r.Succeed("ERP-2", T0));
        Assert.Throws<ConflictException>(() => r.FailTransient("x", null, T0, null, 0));
        Assert.Throws<ConflictException>(() => r.DeadLetterNow("x", null, T0));
    }

    [Fact]
    public void The_idempotency_key_names_tenant_entity_type_object_and_version()
    {
        var t = Guid.NewGuid();
        var e = Guid.NewGuid();
        var o = Guid.NewGuid();
        var key = ErpSyncRecord.KeyFor(t, e, SyncType.PostInvoice, o, "3");
        Assert.Equal($"{t:N}:{e:N}:PostInvoice:{o:N}:3", key);
        Assert.NotEqual(key, ErpSyncRecord.KeyFor(t, e, SyncType.PostInvoice, o, "4"));
        Assert.NotEqual(key, ErpSyncRecord.KeyFor(Guid.NewGuid(), e, SyncType.PostInvoice, o, "3"));
    }
}

public sealed class InvoiceMapperTests
{
    private static PostingInvoice Invoice(params PostingLine[] lines) => new(
        new PayableInvoice(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "INV-1", new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "SAR", 115m, "Approved", 1, 1, DateTimeOffset.UtcNow, "h", []),
        null, 100m, 15m, lines, []);

    private static PostingLine Line(string? tax = "S", Dictionary<string, string>? dims = null) => new(1, "Consulting", 1, 100m, 100m, tax, 15m, dims ?? new Dictionary<string, string>());

    private static ErpCompanyMapping Mapping(Dictionary<string, string>? tax = null, params DimensionMapping[] dims) =>
        new("USMF", "VENDINV", null, new Dictionary<string, string>(), tax ?? new Dictionary<string, string>(), dims);

    [Fact]
    public void A_plain_invoice_maps_with_the_document_key_and_vendor_account()
    {
        var m = InvoiceMapper.Map(Invoice(Line()), "V-001", "key-1", Mapping());
        Assert.Null(m.Problem);
        Assert.Equal("key-1", m.Invoice!.DocumentKey);
        Assert.Equal("V-001", m.Invoice.VendorAccount);
        Assert.Equal(115m, m.Invoice.Total);
        Assert.Equal("S", m.Invoice.Lines.Single().TaxCode);
    }

    [Fact]
    public void Tax_codes_are_translated_and_an_unmapped_code_is_refused_when_a_map_exists()
    {
        var ok = InvoiceMapper.Map(Invoice(Line("S")), "V", "k", Mapping(new() { ["S"] = "VAT15" }));
        Assert.Equal("VAT15", ok.Invoice!.Lines.Single().TaxCode);

        var bad = InvoiceMapper.Map(Invoice(Line("Z")), "V", "k", Mapping(new() { ["S"] = "VAT15" }));
        Assert.Equal("tax_code_unmapped", bad.Problem!.Code);
        Assert.Null(bad.Invoice);
    }

    [Fact]
    public void Dimensions_are_translated_defaulted_and_never_guessed()
    {
        var map = new DimensionMapping("CostCenter", "CostCentre", "CC-DEFAULT", new Dictionary<string, string> { ["100"] = "CC-100" });
        var withValue = InvoiceMapper.Map(Invoice(Line(dims: new() { ["CostCenter"] = "100" })), "V", "k", Mapping(null, map));
        Assert.Equal("CC-100", withValue.Invoice!.Lines.Single().Dimensions["CostCentre"]);

        var defaulted = InvoiceMapper.Map(Invoice(Line()), "V", "k", Mapping(null, map));
        Assert.Equal("CC-DEFAULT", defaulted.Invoice!.Lines.Single().Dimensions["CostCentre"]);

        var unknownValue = InvoiceMapper.Map(Invoice(Line(dims: new() { ["CostCenter"] = "999" })), "V", "k", Mapping(null, map));
        Assert.Equal("dimension_value_unmapped", unknownValue.Problem!.Code);

        var unmappedDimension = InvoiceMapper.Map(Invoice(Line(dims: new() { ["Project"] = "P1" })), "V", "k", Mapping(null, map));
        Assert.Equal("dimension_unmapped", unmappedDimension.Problem!.Code);
    }

    [Fact]
    public void An_invoice_without_lines_is_not_posted() =>
        Assert.Equal("no_lines", InvoiceMapper.Map(Invoice(), "V", "k", Mapping()).Problem!.Code);
}
