using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Application;
using Vpms.Identity.Contracts;

namespace Vpms.UnitTests.Security;

public class SegregationOfDutiesTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    [Fact]
    public void Maker_cannot_check_their_own_work()
    {
        var ex = Assert.Throws<SegregationOfDutiesException>(() => MakerCheckerGuard.EnsureDifferent(Alice, Alice, "approve invoice"));
        Assert.Equal("maker-checker", ex.Rule);
    }

    [Fact]
    public void Different_people_pass_maker_checker() => MakerCheckerGuard.EnsureDifferent(Alice, Bob, "approve invoice");

    [Fact]
    public void Unidentified_actors_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => MakerCheckerGuard.EnsureDifferent(Guid.Empty, Bob, "x"));
        Assert.Throws<ArgumentException>(() => MakerCheckerGuard.EnsureDifferent(Alice, Guid.Empty, "x"));
    }

    [Fact]
    public void Releaser_must_differ_from_every_prior_actor()
    {
        PriorActor[] prior = [new("invoice approver", Alice), new("proposal creator", Bob)];
        DifferentPersonRule.Ensure(Guid.NewGuid(), "release payment", prior);
        Assert.Throws<SegregationOfDutiesException>(() => DifferentPersonRule.Ensure(Alice, "release payment", prior));
        Assert.Throws<SegregationOfDutiesException>(() => DifferentPersonRule.Ensure(Bob, "release payment", prior));
    }

    [Theory]
    [InlineData(Permissions.InvoiceApprove, Permissions.PaymentRelease)]
    [InlineData(Permissions.VendorBankDetailsEdit, Permissions.VendorBankDetailsApprove)]
    [InlineData(Permissions.PaymentPrepare, Permissions.PaymentRelease)]
    [InlineData(Permissions.InvoiceCreate, Permissions.InvoiceApprove)]
    public void Default_matrix_blocks_the_mandatory_conflicts_in_both_orders(string a, string b)
    {
        var matrix = SodConfiguration.Default();
        Assert.Throws<SegregationOfDutiesException>(() => matrix.EnsureNoConflicts(new HashSet<string> { a, b }));
        Assert.Throws<SegregationOfDutiesException>(() => matrix.EnsureNoConflicts(new HashSet<string> { b, a }));
        matrix.EnsureNoConflicts(new HashSet<string> { a });
    }

    [Fact]
    public void Every_conflict_references_a_known_permission()
    {
        foreach (var c in SodConfiguration.Default().Conflicts)
        {
            Assert.Contains(c.First, Permissions.All);
            Assert.Contains(c.Second, Permissions.All);
        }
    }

    [Fact]
    public void System_roles_use_only_catalogue_permissions_and_never_conflict()
    {
        var matrix = SodConfiguration.Default();
        foreach (var (name, permissions) in Vpms.Identity.Domain.SystemRoles.Definitions)
        {
            Assert.All(permissions, p => Assert.Contains(p, Permissions.All));
            Assert.Empty(matrix.FindConflicts(permissions.ToHashSet()));
            Assert.True(name.Length > 0);
        }
    }
}
