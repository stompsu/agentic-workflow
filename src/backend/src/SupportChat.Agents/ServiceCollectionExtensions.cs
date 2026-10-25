using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SupportChat.Agents.Prompts;
using SupportChat.Agents.Workflows.Echo;
using SupportChat.Agents.Workflows.SupportTicket;
using SupportChat.Core.Options;
using SupportChat.Core.Prompts;
using SupportChat.Core.Workflows;

namespace SupportChat.Agents;

public static class ServiceCollectionExtensions
{
    /// <summary>The "one registration" for a workflow type (ADR 0002 section 2).</summary>
    public static IServiceCollection AddWorkflow<TWorkflow>(this IServiceCollection services)
        where TWorkflow : class, IWorkflowDefinition
        => services.AddSingleton<IWorkflowDefinition, TWorkflow>();

    /// <summary>Registers only the registry. Workflow registrations (AddWorkflow) are added by the caller.</summary>
    public static IServiceCollection AddAgentWorkflows(this IServiceCollection services)
    {
        services.TryAddSingleton<IWorkflowRegistry, WorkflowRegistry>();
        return services;
    }

    /// <summary>
    /// Registry + prompt provider + all product workflows. Adding a workflow type = one new class + one
    /// <c>AddWorkflow&lt;T&gt;()</c> line here. Requires IChatClientFactory and IKnowledgeSearch (Infrastructure).
    /// </summary>
    public static IServiceCollection AddAgentWorkflows(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAgentWorkflows();
        services.TryAddSingleton<IPromptProvider, EmbeddedPromptProvider>();

        services.AddOptions<SupportTicketOptions>()
            .Bind(configuration.GetSection(SupportTicketOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SupportTicketOptions>, SupportTicketOptionsValidator>());

        services.AddWorkflow<SupportTicketWorkflow>();

        if (configuration.GetValue(EchoWorkflowOptions.SectionName + ":Enabled", false))
        {
            services.AddWorkflow<EchoWorkflow>();
        }

        return services;
    }
}
