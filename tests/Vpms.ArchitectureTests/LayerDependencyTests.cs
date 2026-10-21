using System.Reflection;
using NetArchTest.Rules;

namespace Vpms.ArchitectureTests;

public class LayerDependencyTests
{
    private static readonly string[] Modules = ["Identity", "CustomerAdmin", "Audit"];

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
