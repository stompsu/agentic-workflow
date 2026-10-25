using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupportChat.Core.Workflows;

namespace SupportChat.Agents;

public static class ServiceCollectionExtensions
{
    /// <summary>The "one registration" for a workflow type (ADR 0002 section 2).</summary>
    public static IServiceCollection AddWorkflow<TWorkflow>(this IServiceCollection services)
        where TWorkflow : class, IWorkflowDefinition
        => services.AddSingleton<IWorkflowDefinition, TWorkflow>();

    /// <summary>Registers the registry. Workflow registrations (AddWorkflow) are added by the host / agent-workflows.</summary>
    public static IServiceCollection AddAgentWorkflows(this IServiceCollection services)
    {
        services.TryAddSingleton<IWorkflowRegistry, WorkflowRegistry>();
        return services;
    }
}
