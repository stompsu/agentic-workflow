using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Api.Chat;

/// <summary>Fail fast on limits that would break the stream loop (zero heartbeat = busy loop).</summary>
public sealed class ChatOptionsValidator : IValidateOptions<ChatLimitsOptions>
{
    public ValidateOptionsResult Validate(string? name, ChatLimitsOptions o)
    {
        var failures = new List<string>();
        if (o.TurnTimeout <= TimeSpan.Zero)
        {
            failures.Add("Chat:TurnTimeout must be positive.");
        }

        if (o.HeartbeatInterval <= TimeSpan.Zero)
        {
            failures.Add("Chat:HeartbeatInterval must be positive.");
        }

        if (o.PersistTimeout <= TimeSpan.Zero)
        {
            failures.Add("Chat:PersistTimeout must be positive.");
        }

        if (o.TurnsPerMinute < 1)
        {
            failures.Add("Chat:TurnsPerMinute must be at least 1.");
        }

        if (o.MaxMessageChars < 1)
        {
            failures.Add("Chat:MaxMessageChars must be at least 1.");
        }

        if (o.HistoryMaxMessages < 0 || o.HistoryMaxTokens < 0)
        {
            failures.Add("Chat:HistoryMaxMessages and Chat:HistoryMaxTokens must not be negative.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
