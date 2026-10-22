namespace Vpms.Workflow.Domain.Errors;

/// <summary>A workflow definition that cannot be stored or run. Maps to HTTP 400.</summary>
public sealed class WorkflowDefinitionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
