using Vpms.Workflow.Domain;

namespace Vpms.UnitTests.Workflow;

public class MaterialityTests
{
    private static Dictionary<string, string> Snap(string total = "1000", string tax = "150", string currency = "SAR", string po = "PO-1", string number = "INV-1", string lines = "2") => new()
    {
        ["total"] = total, ["taxTotal"] = tax, ["currency"] = currency, ["vendor"] = "v1", ["number"] = number, ["date"] = "2026-01-01", ["dueDate"] = "2026-02-01",
        ["poReference"] = po, ["supplierTaxNumber"] = "300000000000003", ["lineCount"] = lines, ["dimensions"] = "CostCenter=IT",
    };

    private static MaterialityOutcome Eval(MaterialityConfig c, Dictionary<string, string> before, Dictionary<string, string> after) => Materiality.Evaluate(c, before, after);

    [Fact]
    public void By_default_every_change_is_material_and_no_change_is_not()
    {
        var d = MaterialityConfig.Defaults();
        Assert.False(Eval(d, Snap(), Snap()).Material);
        Assert.Equal(["total"], Eval(d, Snap(), Snap(total: "1000.01")).ChangedFields);
        Assert.Equal(["currency"], Eval(d, Snap(), Snap(currency: "USD")).ChangedFields);
        Assert.Equal(["poReference"], Eval(d, Snap(), Snap(po: "PO-2")).ChangedFields);
        Assert.Equal(["lineCount"], Eval(d, Snap(), Snap(lines: "3")).ChangedFields);
    }

    [Theory]
    [InlineData("1000", false)]
    [InlineData("1010", false)]     // exactly 1% of 1000
    [InlineData("990", false)]      // a decrease is judged the same way
    [InlineData("1010.01", true)]
    [InlineData("989.99", true)]
    public void Percentage_allowance_has_an_inclusive_edge(string newTotal, bool material)
    {
        var c = new MaterialityConfig(1m, 0m, ["total"]);
        Assert.Equal(material, Eval(c, Snap(), Snap(total: newTotal)).Material);
    }

    [Theory]
    [InlineData("1005", false)]
    [InlineData("1005.01", true)]
    public void Absolute_allowance_has_an_inclusive_edge(string newTotal, bool material)
    {
        var c = new MaterialityConfig(0m, 5m, ["total"]);
        Assert.Equal(material, Eval(c, Snap(), Snap(total: newTotal)).Material);
    }

    [Fact]
    public void The_larger_of_the_two_allowances_applies()
    {
        var c = new MaterialityConfig(1m, 50m, ["total"]);   // 1% of 1000 = 10, absolute 50
        Assert.False(Eval(c, Snap(), Snap(total: "1050")).Material);
        Assert.True(Eval(c, Snap(), Snap(total: "1050.01")).Material);
    }

    [Fact]
    public void Only_the_configured_fields_count()
    {
        var c = new MaterialityConfig(0m, 0m, ["total", "currency"]);
        Assert.False(Eval(c, Snap(), Snap(po: "PO-9", number: "INV-9", lines: "9")).Material);
        Assert.Equal(["total", "currency"], Eval(c, Snap(), Snap(total: "2000", currency: "USD")).ChangedFields);
    }

    [Fact]
    public void Text_comparison_ignores_case_and_surrounding_spaces()
    {
        var c = MaterialityConfig.Defaults();
        Assert.False(Eval(c, Snap(number: "INV-1"), Snap(number: " inv-1 ")).Material);
        Assert.True(Eval(c, Snap(number: "INV-1"), Snap(number: "INV-10")).Material);
    }

    [Fact]
    public void Missing_fields_count_as_empty()
    {
        var c = MaterialityConfig.Defaults();
        var before = new Dictionary<string, string>();
        var after = new Dictionary<string, string> { ["poReference"] = "PO-1" };
        Assert.Equal(["poReference"], Eval(c, before, after).ChangedFields);
    }

    [Fact]
    public void Settings_are_validated()
    {
        Assert.Empty(MaterialityConfig.Defaults().Validate());
        Assert.Contains(new MaterialityConfig(-1, 0, ["total"]).Validate(), e => e.Code == "percent_invalid");
        Assert.Contains(new MaterialityConfig(101, 0, ["total"]).Validate(), e => e.Code == "percent_invalid");
        Assert.Contains(new MaterialityConfig(0, -1, ["total"]).Validate(), e => e.Code == "absolute_invalid");
        Assert.Contains(new MaterialityConfig(0, 0, ["colour"]).Validate(), e => e.Code == "field_unknown");
        Assert.Contains(new MaterialityConfig(0, 0, ["total", "total"]).Validate(), e => e.Code == "field_duplicate");
    }

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var c = new MaterialityConfig(2.5m, 100m, ["total", "poReference"]);
        var back = MaterialityConfig.Parse(c.ToJson());
        Assert.Equal(2.5m, back.AmountPercent);
        Assert.Equal(100m, back.AmountAbsolute);
        Assert.Equal(["total", "poReference"], back.MaterialFields);
    }

    [Fact]
    public void Rebinding_follows_the_edit_and_removes_the_editor_from_open_tasks()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var def = WorkflowKit.Def("s", WorkflowKit.Approval("s", null, ApprovalMode.AnyOne, 0, WorkflowKit.User(a), WorkflowKit.User(editor)));
        var run = WorkflowRun.Start(def, WorkflowKit.Facts(100), [WorkflowKit.Maker], WorkflowKit.Maker, WorkflowKit.Resolver(), DateTimeOffset.UnixEpoch);
        run.SubjectVersion = 1;

        run.Rebind(2, 105m, editor);
        Assert.Equal(2, run.SubjectVersion);
        Assert.Equal(105m, run.Facts.Amount);
        Assert.Contains(editor, run.Makers);
        Assert.Equal([a], run.Tasks[0].Assignees);
        Assert.Throws<Vpms.BuildingBlocks.Security.SegregationOfDutiesException>(() =>
            run.Decide(def, run.Tasks[0].Id, new DecisionRequest(editor, DecisionKind.Approve, null, DateTimeOffset.UnixEpoch), WorkflowKit.Resolver()));
        _ = b;
    }
}
