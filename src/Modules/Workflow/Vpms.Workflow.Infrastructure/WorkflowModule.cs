using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.Workflow.Application;
using Vpms.Workflow.Contracts;

namespace Vpms.Workflow.Infrastructure;

public static class WorkflowModule
{
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<WorkflowDbContext>(connectionString);
        services.AddScoped<ApproverResolverFactory>();
        services.AddScoped<WorkflowDefinitionService>();
        services.AddScoped<IWorkflowDefinitionService>(sp => sp.GetRequiredService<WorkflowDefinitionService>());
        services.AddScoped<ApprovalService>();
        services.AddScoped<IApprovalService>(sp => sp.GetRequiredService<ApprovalService>());
        services.AddScoped<IApprovalStarter>(sp => sp.GetRequiredService<ApprovalService>());
        services.AddScoped<MaterialityPolicyService>();
        services.AddScoped<IMaterialityPolicyService>(sp => sp.GetRequiredService<MaterialityPolicyService>());
        services.AddScoped<IMaterialityEvaluator>(sp => sp.GetRequiredService<MaterialityPolicyService>());
        services.AddScoped<IApprovalHistory>(sp => sp.GetRequiredService<ApprovalService>());
        services.AddScoped<IApprovalLifecycle>(sp => sp.GetRequiredService<ApprovalService>());
        services.AddScoped<IDelegationService, DelegationService>();
        services.AddScoped<WorkflowTimers>();
        services.AddScoped<IWorkflowTimers>(sp => sp.GetRequiredService<WorkflowTimers>());
        services.AddScoped<IApprovalScan>(sp => sp.GetRequiredService<ApprovalService>());
        return services;
    }
}
