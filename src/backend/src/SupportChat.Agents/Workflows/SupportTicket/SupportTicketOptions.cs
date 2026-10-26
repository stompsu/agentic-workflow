using Microsoft.Extensions.Options;
using SupportChat.Core.Options;

namespace SupportChat.Agents.Workflows.SupportTicket;

/// <summary>Section "Workflows:SupportTicket". Agent keys: Orchestrator, Policy, Claims, HealthAssessments.</summary>
public sealed class SupportTicketOptions
{
    public const string SectionName = "Workflows:SupportTicket";

    public double ConfidenceThreshold { get; set; } = 0.7;

    public int MaxConsecutiveClarifications { get; set; } = 2;

    /// <summary>Max search_knowledge round trips per specialist turn (1..10).</summary>
    public int MaxToolCallsPerTurn { get; set; } = 3;

    public Dictionary<string, SupportTicketAgentOptions> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SupportTicketAgentOptions
{
    /// <summary>Model deployment name in the Foundry project (required).</summary>
    public string? Deployment { get; set; }

    /// <summary>Optional. Orchestrator defaults to 0; specialists leave the model default.</summary>
    public float? Temperature { get; set; }
}

public sealed class SupportTicketOptionsValidator : IValidateOptions<SupportTicketOptions>
{
    public ValidateOptionsResult Validate(string? name, SupportTicketOptions options)
    {
        var errors = new List<string>();
        if (double.IsNaN(options.ConfidenceThreshold) || options.ConfidenceThreshold is < 0 or > 1)
        {
            errors.Add($"{SupportTicketOptions.SectionName}:ConfidenceThreshold must be between 0 and 1.");
        }

        if (options.MaxConsecutiveClarifications < 0)
        {
            errors.Add($"{SupportTicketOptions.SectionName}:MaxConsecutiveClarifications must be >= 0.");
        }

        if (options.MaxToolCallsPerTurn is < 1 or > 10)
        {
            errors.Add($"{SupportTicketOptions.SectionName}:MaxToolCallsPerTurn must be between 1 and 10.");
        }

        foreach (var key in SupportTicketWorkflow.AgentKeys)
        {
            if (!options.Agents.TryGetValue(key, out var agent) || string.IsNullOrWhiteSpace(agent.Deployment)
                || ConfigPlaceholders.HasPlaceholder(agent.Deployment))
            {
                errors.Add($"{SupportTicketOptions.SectionName}:Agents:{key}:Deployment is required and must not be a placeholder.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
