using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vpms.Identity.Contracts;

namespace Vpms.ArchitectureTests;

/// <summary>
/// Every HTTP action must make an authorization decision on purpose. A signed-in staff member passes the global fallback policy, so an action that
/// forgets its permission check would be open to every user of the tenant. An action is accepted when it has [AllowAnonymous], a permission
/// policy, a vendor-portal policy, calls the permission evaluator (directly or through a helper in its controller), or is listed below with the reason
/// it needs nothing more.
/// </summary>
public class EndpointAuthorizationTests
{
    /// <summary>Actions that are deliberately open to any signed-in user of the tenant. Adding to this list is a decision a reviewer must see.</summary>
    private static readonly Dictionary<string, string> OwnDataOnly = new()
    {
        ["TenantProfileController.Get"] = "Shows the caller's own organisation name and settings that the whole UI needs.",
        ["NotificationsController.List"] = "Returns only the caller's own notifications.",
        ["NotificationsController.UnreadCount"] = "Counts only the caller's own notifications.",
        ["NotificationsController.Read"] = "Changes only the caller's own notification (the service looks it up by the caller's id).",
        ["NotificationsController.ReadAll"] = "Changes only the caller's own notifications.",
        ["NotificationsController.Preferences"] = "Reads only the caller's own preferences.",
        ["NotificationsController.SetPreference"] = "Changes only the caller's own preferences; mandatory kinds are enforced in the domain.",
        ["DelegationsController.Read"] = "Marks only the caller's own approval notice as read (the service looks it up by the caller's id).",
        ["MaterialityController.Fields"] = "Static list of field names; no tenant data.",
        ["VerificationRulesController.Catalog"] = "Static catalogue of rule definitions; no tenant data.",
    };

    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
                .Select(m => (t, m)));

    [Fact]
    public void Every_action_makes_an_authorization_decision()
    {
        var undecided = new List<string>();
        foreach (var (controller, action) in Actions())
        {
            var key = controller.Name + "." + action.Name;
            if (HasAttribute<AllowAnonymousAttribute>(controller, action)) continue;
            if (HasAuthorizePolicy(controller, action)) continue;
            if (OwnDataOnly.ContainsKey(key)) continue;
            if (CallsPermissionEvaluator(controller, action)) continue;
            undecided.Add(key);
        }

        Assert.True(undecided.Count == 0,
            "These actions rely on the global fallback policy alone (any signed-in user of the tenant). Give each a permission check, or list it with a reason: "
            + string.Join(", ", undecided));
    }

    [Fact]
    public void The_allow_list_only_names_real_actions()
    {
        var real = Actions().Select(a => a.Controller.Name + "." + a.Action.Name).ToHashSet();
        Assert.All(OwnDataOnly.Keys, k => Assert.Contains(k, real));
        Assert.All(OwnDataOnly.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void The_detector_flags_an_action_that_checks_nothing()
    {
        var bare = typeof(Probe).GetMethod(nameof(Probe.Bare))!;
        var guarded = typeof(Probe).GetMethod(nameof(Probe.Guarded))!;
        Assert.False(CallsPermissionEvaluator(typeof(Probe), bare));
        Assert.True(CallsPermissionEvaluator(typeof(Probe), guarded));
    }

    private sealed class Probe(IPermissionEvaluator permissions) : ControllerBase
    {
        [HttpGet]
        public IActionResult Bare() => Ok();

        [HttpGet]
        public async Task<IActionResult> Guarded()
        {
            await permissions.HasAsync(Guid.Empty, "x");
            return Ok();
        }
    }

    private static bool HasAttribute<T>(Type controller, MethodInfo action) where T : Attribute =>
        action.GetCustomAttributes<T>(true).Any() || controller.GetCustomAttributes<T>(true).Any();

    private static bool HasAuthorizePolicy(Type controller, MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>(true).Concat(controller.GetCustomAttributes<AuthorizeAttribute>(true))
            .Any(a => !string.IsNullOrEmpty(a.Policy) || !string.IsNullOrEmpty(a.Roles));

    /// <summary>True when the action, its async state machine, or a helper it reaches in the API project (for example the ScopeFor extension) calls IPermissionEvaluator.</summary>
    private static bool CallsPermissionEvaluator(Type controller, MethodInfo action)
    {
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>();
        queue.Enqueue(action);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            if (!seen.Add(method)) continue;
            foreach (var body in BodiesOf(method.DeclaringType ?? controller, method))
            {
                foreach (var called in body.Module.ResolveMethodsReferenced(body))
                {
                    if (called.DeclaringType == typeof(IPermissionEvaluator) || called.DeclaringType?.GetInterfaces().Contains(typeof(IPermissionEvaluator)) == true) return true;
                    if (called.DeclaringType?.Assembly == controller.Assembly) queue.Enqueue(called);
                }
            }
        }

        return false;
    }

    private static IEnumerable<MethodInfo> BodiesOf(Type controller, MethodBase method)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        if (method is MethodInfo mi && mi.GetMethodBody() is not null) yield return mi;
        // Async methods and lambdas live in compiler-generated nested types named after the method.
        foreach (var nested in controller.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!nested.Name.Contains(method.Name, StringComparison.Ordinal) && !nested.Name.StartsWith("<>c", StringComparison.Ordinal)) continue;
            foreach (var m in nested.GetMethods(all))
            {
                if (m.GetMethodBody() is not null) yield return m;
            }
        }
    }
}
