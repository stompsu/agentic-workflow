using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SupportChat.Core.Workflows;

namespace SupportChat.Agents.Workflows.Echo;

/// <summary>
/// Streams the user's message back word by word. No model, no search. Proves that a workflow type needs only one class and
/// one registration (ADR 0002 section 10). Registered only when Workflows:Echo:Enabled = true.
/// </summary>
public sealed partial class EchoWorkflow : IWorkflowDefinition
{
    public const string WorkflowId = "echo";
    public const string AgentName = "Echo";

    public string Id => WorkflowId;

    public string DisplayName => "Echo";

    public string Description => "Repeats your message word by word. For development and tests only.";

    public async IAsyncEnumerable<ChatStreamEvent> RunAsync(
        WorkflowRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AgentChanged(AgentName);

        var any = false;
        foreach (Match word in Words().Matches(request.UserMessage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            any = true;
            yield return new TokenDelta(word.Value);
            // Yield to the scheduler so that a consumer can observe cancellation between words.
            await Task.Yield();
        }

        if (!any)
        {
            yield return new TokenDelta("(empty message)");
        }

        yield return new TurnCompleted();
    }

    // Each token is a word plus its trailing whitespace, so concatenated tokens reproduce the message.
    [GeneratedRegex(@"\s*\S+\s*")]
    private static partial Regex Words();
}
