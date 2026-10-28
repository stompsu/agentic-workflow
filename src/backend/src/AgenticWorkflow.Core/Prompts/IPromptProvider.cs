namespace AgenticWorkflow.Core.Prompts;

public interface IPromptProvider
{
    /// <summary>Returns the prompt file content, e.g. "Workflows/SupportTicket/Prompts/orchestrator.v1.md". Throws if missing.</summary>
    string Get(string path);
}
