using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SupportChat.LocalHost.Knowledge;

namespace SupportChat.LocalHost.Models;

/// <summary>
/// Deterministic, network-free <see cref="IChatClient"/> for local mode (ADR 0005 section 4). It is NOT an AI model: it
/// classifies by whole-token keyword matching, issues one <c>search_knowledge</c> call and builds the answer from the tool
/// result. Behaviour depends on the call shape only:
/// 1. <c>ResponseFormat is ChatResponseFormatJson</c> -> classification JSON.
/// 2. Tools contain <c>search_knowledge</c>, no function result after the last user message -> a tool call.
/// 3. Same, with a function result after the last user message -> a cited answer.
/// 4. Anything else -> <see cref="InvalidOperationException"/>.
/// Streaming and non-streaming produce identical content; streaming adds the word-by-word delay.
/// </summary>
public sealed partial class ScriptedChatClient : IChatClient
{
    public const string ToolName = "search_knowledge";
    public const string ScriptedFooter = "_Scripted local answer: no AI model was used._";
    public const string NoResultsAnswer = "I could not find this in the knowledge base.";
    public const string ClarifyingQuestion = "Is your question about your policy, a claim, or a health assessment?";
    public static readonly TimeSpan MaxTokenDelay = TimeSpan.FromSeconds(1);

    private const int MaxQueryChars = 200;
    private const int MaxSentenceChars = 400;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Keywords are normalised with the same tokenizer as the message, so "status" and "statuses" meet at one token.
    private static readonly (string Category, HashSet<string> Keywords)[] Categories =
    [
        ("policy", Normalize("policy coverage cover premium exclusion cancel cooling renew grace")),
        ("claims", Normalize("claim reimburse appeal receipt document payout status rejected declined")),
        ("healthAssessments", Normalize("health assessment booking appointment result checkup blood preparation")),
    ];

    private readonly TimeSpan _tokenDelay;

    public ScriptedChatClient(TimeSpan tokenDelay = default)
    {
        if (tokenDelay < TimeSpan.Zero || tokenDelay > MaxTokenDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenDelay), "TokenDelay must be between 00:00:00 and 00:00:01.");
        }

        _tokenDelay = tokenDelay;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = Plan(messages.ToList(), options);
        var contents = reply.Contents.Count > 0 ? reply.Contents : [new TextContent(reply.Text)];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents.ToList())));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = Plan(messages.ToList(), options);
        if (reply.Contents.Count > 0)
        {
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = reply.Contents.ToList() };
            yield break;
        }

        if (!reply.Stream)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply.Text);
            yield break;
        }

        foreach (Match piece in Pieces().Matches(reply.Text))
        {
            if (_tokenDelay > TimeSpan.Zero)
            {
                await Task.Delay(_tokenDelay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece.Value);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    // ---- planning (pure) ----

    private sealed record Reply(string Text, IReadOnlyList<AIContent> Contents, bool Stream);

    private static Reply Plan(List<ChatMessage> messages, ChatOptions? options)
    {
        if (options?.ResponseFormat is ChatResponseFormatJson)
        {
            return new Reply(Classify(messages, options), [], false);
        }

        if (options?.Tools?.Any(t => t.Name == ToolName) == true)
        {
            var lastUser = messages.FindLastIndex(m => m.Role == ChatRole.User);
            var result = lastUser < 0
                ? null
                : messages.Skip(lastUser + 1).SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (result is null)
            {
                var query = lastUser < 0 ? string.Empty : messages[lastUser].Text;
                query = query.Trim();
                if (query.Length > MaxQueryChars)
                {
                    query = query[..MaxQueryChars];
                }

                var call = new FunctionCallContent(
                    "scripted-search-1", ToolName, new Dictionary<string, object?> { ["query"] = query });
                return new Reply(string.Empty, [call], false);
            }

            return new Reply(BuildAnswer(ResultText(result)), [], true);
        }

        throw new InvalidOperationException(
            "ScriptedChatClient supports only classification calls (JSON response format) and specialist calls with the search_knowledge tool.");
    }

    // ---- classification ----

    private static string Classify(List<ChatMessage> messages, ChatOptions options)
    {
        var users = messages.Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text)).Select(m => m.Text).ToList();
        var last = users.Count > 0 ? users[^1] : string.Empty;

        var hits = Count(Tokenize(last));
        if (hits.Sum() > 0)
        {
            return Decide(hits);
        }

        var previous = LastCategory(options.Instructions, messages);
        if (previous is not null)
        {
            return Result(previous, 0.8, "follow-up (scripted)", null);
        }

        var earlier = Tokenize(string.Join(' ', users.SkipLast(1).TakeLast(2)));
        return Decide(Count(earlier));
    }

    private static string Decide(int[] hits)
    {
        var best = Array.IndexOf(hits, hits.Max());
        if (hits[best] > 0 && hits.Count(h => h == hits[best]) == 1)
        {
            return Result(Categories[best].Category, 0.9, "keyword match (scripted)", null);
        }

        return Result("unknown", 0.3, "ambiguous or no keyword match (scripted)", ClarifyingQuestion);
    }

    private static string Result(string category, double confidence, string rationale, string? question) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["category"] = category,
            ["confidence"] = confidence,
            ["rationale"] = rationale,
            ["clarifyingQuestion"] = question,
        });

    private static int[] Count(IReadOnlyCollection<string> tokens)
    {
        var distinct = tokens.ToHashSet(StringComparer.Ordinal);
        return Categories.Select(c => c.Keywords.Count(distinct.Contains)).ToArray();
    }

    /// <summary>Parses the sentence "The category routed on the previous answered turn was: X." from the instructions.</summary>
    internal static string? LastCategory(string? instructions, IEnumerable<ChatMessage> messages)
    {
        var sources = new List<string>();
        if (!string.IsNullOrEmpty(instructions))
        {
            sources.Add(instructions);
        }

        sources.AddRange(messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        foreach (var source in sources)
        {
            var match = LastCategorySentence().Match(source);
            if (match.Success && Categories.Any(c => c.Category == match.Groups[1].Value))
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    internal static List<string> Tokenize(string text) => KnowledgeTokenizer.Tokenize(text);

    private static HashSet<string> Normalize(string keywords) => Tokenize(keywords).ToHashSet(StringComparer.Ordinal);

    // ---- answer ----

    private static string ResultText(FunctionResultContent result) => result.Result switch
    {
        null => string.Empty,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
        var other => other.ToString() ?? string.Empty,
    };

    private sealed record Source(string Ref, string Title, string Text);

    private static string BuildAnswer(string toolJson)
    {
        var sources = ParseSources(toolJson);
        if (sources.Count == 0)
        {
            return NoResultsAnswer;
        }

        var first = sources[0];
        var body = new StringBuilder();
        body.Append(string.Join(' ', Sentences(first.Text).Take(2))).Append(' ').Append('[').Append(first.Ref).Append(']');

        var second = sources.Skip(1).FirstOrDefault(s => !string.Equals(s.Title, first.Title, StringComparison.Ordinal));
        if (second is not null)
        {
            body.Append(' ').Append(Sentences(second.Text).First()).Append(' ').Append('[').Append(second.Ref).Append(']');
        }

        body.Append("\n\n").Append(ScriptedFooter);
        return body.ToString();
    }

    private static List<Source> ParseSources(string toolJson)
    {
        var list = new List<Source>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(toolJson);
        }
        catch (JsonException)
        {
            return list;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in results.EnumerateArray())
            {
                var reference = Str(item, "ref");
                if (!RefPattern().IsMatch(reference))
                {
                    continue;
                }

                var text = Clean(Str(item, "content"));
                if (Sentences(text).Any())
                {
                    list.Add(new Source(reference, Str(item, "title"), text));
                }
            }
        }

        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Removes the &lt;source&gt; wrapper, HTML-decodes, drops heading lines and a leading partial sentence (chunk overlap
    /// remnant) and strips every [S&lt;n&gt;] sequence until none is left. Instructions in the text are never interpreted.
    /// </summary>
    internal static string Clean(string content)
    {
        var text = content.Trim();
        var wrapper = SourceWrapper().Match(text);
        if (wrapper.Success)
        {
            text = wrapper.Groups[1].Value;
        }

        text = SourceTag().Replace(WebUtility.HtmlDecode(text), " ");

        var lines = text.Split('\n').Select(l => l.Trim()).SkipWhile(l => l.Length == 0 || l.StartsWith('#')).ToList();
        var flat = string.Join(' ', lines.Select(l => ListMarker().Replace(l, string.Empty)).Where(l => l.Length > 0));
        flat = Whitespace().Replace(flat, " ").Trim();

        if (flat.Length > 0 && char.IsLower(flat[0]))
        {
            var end = SentenceEnd().Match(flat);
            flat = end.Success ? flat[(end.Index + end.Length)..].TrimStart() : string.Empty;
        }

        string previous;
        do
        {
            previous = flat;
            flat = Marker().Replace(flat, string.Empty);
        }
        while (!string.Equals(previous, flat, StringComparison.Ordinal));

        return Whitespace().Replace(flat, " ").Trim();
    }

    private static IEnumerable<string> Sentences(string text) =>
        SentenceSplit().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.Length > MaxSentenceChars ? s[..MaxSentenceChars] : s);

    [GeneratedRegex(@"\s*\S+|\s+")]
    private static partial Regex Pieces();

    [GeneratedRegex(@"previous answered turn was:\s*([A-Za-z]+)\.")]
    private static partial Regex LastCategorySentence();

    [GeneratedRegex(@"^S[1-9][0-9]{0,3}$")]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"^<source\b[^>]*>(.*)</source>$", RegexOptions.Singleline)]
    private static partial Regex SourceWrapper();

    [GeneratedRegex(@"</?source\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SourceTag();

    [GeneratedRegex(@"\[\s*S\s*\d+\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[.!?](?=\s|$)\s*")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();
}
