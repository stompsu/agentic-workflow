using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SupportChat.Core.Options;
using SupportChat.Core.Storage;

namespace SupportChat.Infrastructure.Storage;

/// <summary>Rules shared by the Cosmos and in-memory stores so both behave identically.</summary>
internal static partial class TurnRules
{
    public const int AutoTitleLength = 60;
    public const int MaxContinuationTokenLength = 8192;

    /// <summary>A lock older than Chat:TurnTimeout + 60 s is stale and may be taken over (data-model 1.2).</summary>
    public static bool IsStale(DateTimeOffset startedAt, DateTimeOffset now, TimeSpan turnTimeout) =>
        now - startedAt > turnTimeout + ChatLimitsOptions.StaleLockGrace;

    public static string AutoTitle(string content)
    {
        var trimmed = content.Trim();
        return trimmed.Length <= AutoTitleLength ? trimmed : trimmed[..AutoTitleLength].TrimEnd();
    }

    public static string NewId() => Guid.CreateVersion7().ToString();

    public static string RoleName(ChatRole role) => role.Value;

    public static void EnsureUserMessage(string sessionId, MessageRecord message)
    {
        if (message.Role != ChatRole.User || message.SessionId != sessionId)
        {
            throw new ArgumentException("The turn-start message must be a user message of the session.", nameof(message));
        }
    }

    /// <summary>
    /// History steps 1-3 (data-model 1.6) on messages sorted ascending: pairing filter, citation marker stripping.
    /// Assistant: kept only if complete AND directly preceded by a user message (a leading assistant message, whose
    /// user message expired by TTL or fell outside the window, is dropped). User: kept only if the next message is a
    /// kept assistant message.
    /// </summary>
    public static IReadOnlyList<MessageRecord> ApplyHistoryRules(IReadOnlyList<MessageRecord> ascending)
    {
        var result = new List<MessageRecord>();
        for (var i = 0; i < ascending.Count; i++)
        {
            var message = ascending[i];
            if (message.Role == ChatRole.Assistant)
            {
                if (message.Status == MessageStatus.Complete && i > 0 && ascending[i - 1].Role == ChatRole.User)
                {
                    result.Add(message with { Content = CitationMarker().Replace(message.Content, string.Empty) });
                }
            }
            else if (message.Role == ChatRole.User
                     && i + 1 < ascending.Count
                     && ascending[i + 1] is { Role.Value: "assistant", Status: MessageStatus.Complete })
            {
                result.Add(message);
            }
        }

        return result;
    }

    [GeneratedRegex(@"\[S\d+\]")]
    private static partial Regex CitationMarker();
}
