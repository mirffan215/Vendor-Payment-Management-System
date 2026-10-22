using System.Reflection;
using NetArchTest.Rules;

namespace Vpms.ArchitectureTests;

public class LayerDependencyTests
{
    private static readonly string[] Modules = ["Identity", "CustomerAdmin", "Audit", "Documents", "Vendors", "VendorPortal", "Invoices", "Workflow"];

    private static Assembly Asm(string module, string layer) =>
        Assembly.Load($"Vpms.{module}.{layer}");

    [Fact]
    public void Domain_does_not_depend_on_application_or_infrastructure()
    {
        foreach (var m in Modules)
        {
            var result = Types.InAssembly(Asm(m, "Domain")).ShouldNot()
                .HaveDependencyOnAny($"Vpms.{m}.Application", $"Vpms.{m}.Infrastructure",
                    "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
                .GetResult();
            Assert.True(result.IsSuccessful, $"{m}.Domain: {Failing(result)}");
        }
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure()
    {
        foreach (var m in Modules)
        {
            var result = Types.InAssembly(Asm(m, "Application")).ShouldNot()
                .HaveDependencyOn($"Vpms.{m}.Infrastructure").GetResult();
            Assert.True(result.IsSuccessful, $"{m}.Application: {Failing(result)}");
        }
    }

    [Fact]
    public void Modules_only_reference_other_modules_through_Contracts()
    {
        foreach (var m in Modules)
        {
            var others = Modules.Where(o => o != m).ToArray();
            foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Contracts" })
            {
                var forbidden = others.SelectMany(o => new[]
                    { $"Vpms.{o}.Domain", $"Vpms.{o}.Application", $"Vpms.{o}.Infrastructure" }).ToArray();
                var result = Types.InAssembly(Asm(m, layer)).ShouldNot()
                    .HaveDependencyOnAny(forbidden).GetResult();
                Assert.True(result.IsSuccessful, $"{m}.{layer}: {Failing(result)}");
            }
        }
    }

    [Fact]
    public void BuildingBlocks_do_not_depend_on_modules_or_hosts()
    {
        var result = Types.InAssembly(typeof(Vpms.BuildingBlocks.AssemblyMarker).Assembly).ShouldNot()
            .HaveDependencyOnAny("Vpms.Identity", "Vpms.CustomerAdmin", "Vpms.Audit", "Vpms.Api", "Vpms.Worker")
            .GetResult();
        Assert.True(result.IsSuccessful, Failing(result));
    }

    [Fact]
    public void IgnoreQueryFilters_is_not_used_outside_allow_list()
    {
        var allowed = new[] { "Vpms.Audit.Infrastructure", "Vpms.BuildingBlocks" };
        var asms = Modules.SelectMany(m => new[] { "Application", "Infrastructure", "Domain" }.Select(l => Asm(m, l)))
            .Append(typeof(Program).Assembly);
        foreach (var asm in asms)
        {
            if (allowed.Contains(asm.GetName().Name)) continue;
            var offenders = asm.GetTypes().Where(t => ReferencesMethod(t, "IgnoreQueryFilters")).Select(t => t.FullName);
            Assert.Empty(offenders);
        }
    }

    [Fact]
    public void Cross_vendor_checks_are_only_used_by_the_bank_details_fraud_control()
    {
        var asms = Modules.SelectMany(m => new[] { "Application", "Infrastructure", "Domain" }.Select(l => Asm(m, l)))
            .Append(typeof(Program).Assembly);
        var offenders = asms.SelectMany(a => a.GetTypes()).Where(t => ReferencesMethod(t, "BeginCrossVendorCheck"))
            .Select(t => (t.DeclaringType ?? t).FullName).Distinct().ToList(); // async methods compile to nested types
        Assert.Equal(["Vpms.Vendors.Infrastructure.BankDetailsService"], offenders);
    }

    [Fact]
    public void Country_tax_plugins_are_isolated_from_the_modules_that_host_them()
    {
        // Invoices knows only the plug-in interface (in its domain); no Invoices assembly may reference a concrete country module.
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Contracts" })
        {
            var references = Asm("Invoices", layer).GetReferencedAssemblies().Select(a => a.Name);
            Assert.DoesNotContain("Vpms.Tax.Sa", references);
        }

        // A country module may use the shared kernel and the invoice domain, and nothing else from the system.
        var allowed = new[] { "Vpms.Invoices.Domain", "Vpms.BuildingBlocks" };
        var taxReferences = typeof(Vpms.Tax.Sa.SaudiVatProvider).Assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Vpms.", StringComparison.Ordinal));
        Assert.Empty(taxReferences.Except(allowed));
    }

    [Fact]
    public void The_workflow_engine_knows_nothing_about_its_subjects()
    {
        // Invoices (and later payments) start approvals through a contract; the engine never references them back.
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Contracts" })
        {
            var references = Asm("Workflow", layer).GetReferencedAssemblies().Select(a => a.Name!);
            Assert.DoesNotContain(references, n => n.StartsWith("Vpms.Invoices", StringComparison.Ordinal) || n.StartsWith("Vpms.Vendors", StringComparison.Ordinal)
                                                   || n.StartsWith("Vpms.Payments", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void IgnoreQueryFilters_detector_finds_real_usages()
    {
        // Guards the guard: the IL scan above must actually be able to see the call it bans.
        Assert.True(ReferencesMethod(typeof(DeliberateViolation), "IgnoreQueryFilters"));
        Assert.False(ReferencesMethod(typeof(LayerDependencyTests), "ThisMethodDoesNotExist"));
    }

    private sealed class DeliberateViolation
    {
        public static IQueryable<string> Bypass(IQueryable<string> q) =>
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(q);
    }

    private static bool ReferencesMethod(Type t, string name)
    {
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var body = m.GetMethodBody();
            if (body?.GetILAsByteArray() is null) continue;
            if (t.Module.ResolveMethodsReferenced(m).Any(x => x.Name == name)) return true;
        }
        return false;
    }

    private static string Failing(TestResult r) =>
        r.FailingTypeNames is null ? "" : string.Join(", ", r.FailingTypeNames);
}

internal static class ModuleExtensions
{
    public static IEnumerable<MethodBase> ResolveMethodsReferenced(this Module module, MethodInfo m)
    {
        var il = m.GetMethodBody()!.GetILAsByteArray()!;
        for (var i = 0; i < il.Length - 4; i++)
        {
            // call (0x28) / callvirt (0x6F) followed by a metadata token
            if (il[i] != 0x28 && il[i] != 0x6F) continue;
            var token = BitConverter.ToInt32(il, i + 1);
            MethodBase? resolved = null;
            try { resolved = module.ResolveMethod(token, m.DeclaringType?.IsGenericType == true ? m.DeclaringType.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null); }
            catch (ArgumentException) { }
            catch (BadImageFormatException) { }
            if (resolved is not null) yield return resolved;
        }
    }
}
