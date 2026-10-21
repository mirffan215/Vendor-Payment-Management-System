using Vpms.Audit.Domain;

namespace Vpms.UnitTests.Security;

public class AuditHasherTests
{
    private static AuditEvent Sample() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Sequence = 7,
        OccurredAt = new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.Zero).AddTicks(1234567),
        ActorType = "User",
        ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Action = "invoice.approved",
        EntityType = "Invoice",
        EntityId = "INV-1",
        CorrelationId = "corr",
        IpAddress = "10.0.0.1",
        ChangeSummaryJson = "{\"a\":1}",
    };

    [Fact]
    public void Hash_is_deterministic_and_64_hex_chars()
    {
        var h1 = AuditHasher.Compute(AuditHasher.GenesisHash, Sample());
        var h2 = AuditHasher.Compute(AuditHasher.GenesisHash, Sample());
        Assert.Equal(h1, h2);
        Assert.Matches("^[0-9a-f]{64}$", h1);
    }

    [Fact]
    public void Offset_of_the_timestamp_does_not_change_the_hash()
    {
        var a = Sample();
        var b = Sample();
        b.OccurredAt = a.OccurredAt.ToOffset(TimeSpan.FromHours(3));
        Assert.Equal(AuditHasher.Compute(AuditHasher.GenesisHash, a), AuditHasher.Compute(AuditHasher.GenesisHash, b));
    }

    [Theory]
    [InlineData("Action")]
    [InlineData("EntityId")]
    [InlineData("Sequence")]
    [InlineData("ActorId")]
    [InlineData("ChangeSummaryJson")]
    [InlineData("OccurredAt")]
    [InlineData("LegalEntityId")]
    [InlineData("IpAddress")]
    public void Changing_any_field_changes_the_hash(string field)
    {
        var original = AuditHasher.Compute(AuditHasher.GenesisHash, Sample());
        var e = Sample();
        switch (field)
        {
            case "Action": e.Action = "invoice.rejected"; break;
            case "EntityId": e.EntityId = "INV-2"; break;
            case "Sequence": e.Sequence = 8; break;
            case "ActorId": e.ActorId = Guid.NewGuid(); break;
            case "ChangeSummaryJson": e.ChangeSummaryJson = "{\"a\":2}"; break;
            case "OccurredAt": e.OccurredAt = e.OccurredAt.AddTicks(1); break;
            case "LegalEntityId": e.LegalEntityId = Guid.NewGuid(); break;
            case "IpAddress": e.IpAddress = "10.0.0.2"; break;
        }

        Assert.NotEqual(original, AuditHasher.Compute(AuditHasher.GenesisHash, e));
    }

    [Fact]
    public void Previous_hash_is_part_of_the_hash()
    {
        Assert.NotEqual(AuditHasher.Compute(AuditHasher.GenesisHash, Sample()),
            AuditHasher.Compute(new string('a', 64), Sample()));
    }

    [Fact]
    public void Null_and_empty_fields_are_distinguished()
    {
        var a = Sample();
        var b = Sample();
        a.EntityId = null;
        b.EntityId = "";
        Assert.NotEqual(AuditHasher.Compute(AuditHasher.GenesisHash, a), AuditHasher.Compute(AuditHasher.GenesisHash, b));
    }
}
