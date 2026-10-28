using System.Text.RegularExpressions;
using AgenticWorkflow.Core.Knowledge;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Agents.Knowledge;

/// <summary>
/// Per-turn registry of the sources that <c>search_knowledge</c> returned (ADR 0002 section 8). Numbers refs S1..Sn across
/// all tool calls of the turn; resolves [Sn] markers of the final answer and drops markers the tool never returned.
/// Create one instance per turn; it is thread-safe because a model may issue parallel tool calls.
/// </summary>
public sealed partial class CitationCollector
{
    private readonly object _gate = new();
    private readonly List<KnowledgeChunk> _chunks = [];
    private readonly Dictionary<string, string> _refByChunkId = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _chunks.Count;
            }
        }
    }

    /// <summary>Registers a chunk and returns its ref ("S1"). The same chunk id gets the same ref within a turn.</summary>
    public string Register(KnowledgeChunk chunk)
    {
        lock (_gate)
        {
            if (_refByChunkId.TryGetValue(chunk.Id, out var existing))
            {
                return existing;
            }

            _chunks.Add(chunk);
            var reference = "S" + _chunks.Count;
            _refByChunkId[chunk.Id] = reference;
            return reference;
        }
    }

    /// <summary>
    /// Finds [Sn] markers in <paramref name="answerText"/> and returns one event per distinct, known ref in first-appearance
    /// order. Unknown refs (fabricated by the model) are dropped. Title is raw (control characters removed, not encoded);
    /// the url is passed on only if it is absolute http(s).
    /// </summary>
    public IReadOnlyList<CitationFound> Resolve(string answerText)
    {
        var result = new List<CitationFound>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Marker().Matches(answerText))
        {
            var reference = "S" + match.Groups[1].Value;
            if (!seen.Add(reference))
            {
                continue;
            }

            KnowledgeChunk? chunk = null;
            lock (_gate)
            {
                if (int.TryParse(match.Groups[1].ValueSpan, out var n) && n >= 1 && n <= _chunks.Count)
                {
                    chunk = _chunks[n - 1];
                }
            }

            if (chunk is null)
            {
                continue;
            }

            result.Add(new CitationFound(
                reference,
                KnowledgeSanitizer.RemoveControlCharacters(chunk.Title),
                chunk.Id,
                KnowledgeSanitizer.SafeHttpUrl(chunk.SourceUrl)));
        }

        return result;
    }

    [GeneratedRegex(@"\[S([1-9][0-9]{0,3})\]")]
    private static partial Regex Marker();
}
