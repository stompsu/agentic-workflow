using System.Runtime.CompilerServices;
using SupportChat.Core.Workflows;

namespace SupportChat.Api.Tests;

/// <summary>A workflow type that exists only in tests: proves a new type needs one class + one registration.</summary>
public sealed class ThrowawayWorkflow : IWorkflowDefinition
{
    public string Id => "throwaway-test";

    public string DisplayName => "Throwaway";

    public string Description => "A workflow registered only by tests.";

    public async IAsyncEnumerable<ChatStreamEvent> RunAsync(WorkflowRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AgentChanged("Throwaway agent");
        yield return new TokenDelta("hi");
        yield return new TurnCompleted();
        await Task.CompletedTask;
    }
}
