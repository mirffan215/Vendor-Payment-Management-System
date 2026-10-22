using Vpms.Identity.Contracts;
using Vpms.Workflow.Domain;

namespace Vpms.Workflow.Infrastructure;

/// <summary>
/// Builds the engine's synchronous resolver from a snapshot of who holds each role. Only people who hold the invoice-approval
/// permission for the legal entity are ever offered a task, whatever a definition names.
/// </summary>
public sealed class ApproverResolverFactory(IApproverDirectory directory)
{
    public static IEnumerable<ApproverSpec> Approvers(WorkflowDefinitionSpec spec) =>
        spec.Steps.SelectMany(s => s.Approvers.Concat(s.EscalateTo is null ? [] : [s.EscalateTo]));

    public async Task<ApproverResolver> CreateAsync(Guid legalEntityId, WorkflowDefinitionSpec spec, CancellationToken ct = default)
    {
        var approvers = Approvers(spec).ToList();
        var roles = approvers.Where(a => a.Kind == ApproverKind.Role).Select(a => a.Value).Distinct().ToList();
        var holders = roles.Count == 0
            ? new Dictionary<string, IReadOnlyList<Guid>>()
            : (await directory.GetRoleHoldersAsync(legalEntityId, roles, ct)).ToDictionary(k => k.Key, v => v.Value);
        var users = approvers.Where(a => a.Kind == ApproverKind.User).Select(a => Guid.Parse(a.Value)).ToList();

        var candidates = users.Concat(holders.Values.SelectMany(v => v)).Distinct().ToList();
        var permitted = candidates.Count == 0 ? new HashSet<Guid>() : (await directory.FilterByPermissionAsync(legalEntityId, Permissions.InvoiceApprove, candidates, ct)).ToHashSet();

        return a => a.Kind == ApproverKind.User
            ? (permitted.Contains(Guid.Parse(a.Value)) ? [Guid.Parse(a.Value)] : [])
            : (holders.TryGetValue(a.Value, out var us) ? us.Where(permitted.Contains).ToList() : []);
    }
}
