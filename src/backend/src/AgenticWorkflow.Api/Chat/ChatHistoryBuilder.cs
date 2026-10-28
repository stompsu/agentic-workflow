using Microsoft.Extensions.AI;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Chat;

/// <summary>
/// Workflow-agnostic history preparation (data-model 1.6 steps 4-5). The store already applied the pairing filter and
/// marker stripping; this trims oldest-first, in user/assistant pairs, to the token budget and maps to
/// <see cref="HistoryMessage"/>. Token estimate = chars / 4.
/// </summary>
public static class ChatHistoryBuilder
{
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <param name="ascending">Messages oldest first, current user message excluded, pairing filter applied.</param>
    /// <param name="currentMessage">The current user message; its estimate is reserved so it always fits.</param>
    public static IReadOnlyList<HistoryMessage> Build(IReadOnlyList<MessageRecord> ascending, string currentMessage, int maxTokens)
    {
        // Pairs: a user message immediately followed by an assistant message. Anything else (for example an
        // assistant message whose user message fell outside the loaded window) is dropped.
        var pairs = new List<(MessageRecord User, MessageRecord Assistant)>();
        for (var i = 0; i + 1 < ascending.Count; i++)
        {
            if (ascending[i].Role == ChatRole.User && ascending[i + 1].Role == ChatRole.Assistant)
            {
                pairs.Add((ascending[i], ascending[i + 1]));
                i++;
            }
        }

        var budget = Math.Max(0, maxTokens - EstimateTokens(currentMessage));
        var used = 0;
        var firstKept = pairs.Count;
        for (var i = pairs.Count - 1; i >= 0; i--)
        {
            var cost = EstimateTokens(pairs[i].User.Content) + EstimateTokens(pairs[i].Assistant.Content);
            if (used + cost > budget)
            {
                break;
            }

            used += cost;
            firstKept = i;
        }

        var result = new List<HistoryMessage>((pairs.Count - firstKept) * 2);
        for (var i = firstKept; i < pairs.Count; i++)
        {
            result.Add(Map(pairs[i].User));
            result.Add(Map(pairs[i].Assistant));
        }

        return result;
    }

    private static HistoryMessage Map(MessageRecord m) =>
        new(m.Role, m.Content, m.AgentName, m.Kind, m.WorkflowMetadata, m.CreatedAt);
}
