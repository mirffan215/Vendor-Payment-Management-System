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
        services.AddScoped<IApprovalScan>(sp => sp.GetRequiredService<ApprovalService>());
        return services;
    }
}
