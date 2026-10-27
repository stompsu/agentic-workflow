using SupportChat.Core.Knowledge;
using SupportChat.Infrastructure.Search;
using SupportChat.Infrastructure.Search.Source;

namespace SupportChat.LocalHost.Knowledge;

/// <summary>
/// Local replacement of the Azure AI Search query side (ADR 0005 section 3): BM25 (k1 1.2, b 0.75) over the chunks of
/// one domain, statistics per domain, title tokens counted twice. No embeddings and no semantic reranker, so
/// <see cref="KnowledgeChunk.RerankerScore"/> is always null. Content is returned verbatim (sanitisation stays in the
/// search_knowledge tool).
/// </summary>
public sealed class InMemoryKnowledgeSearch : IKnowledgeSearch
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly Lazy<IReadOnlyDictionary<string, DomainIndex>> _domains;
    private readonly int _defaultTop;

    public InMemoryKnowledgeSearch(Func<IReadOnlyList<PlannedChunk>> chunks, int defaultTop)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        _defaultTop = defaultTop;
        _domains = new Lazy<IReadOnlyDictionary<string, DomainIndex>>(
            () => chunks().GroupBy(c => c.Domain, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new DomainIndex(g.ToList()), StringComparer.Ordinal),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public InMemoryKnowledgeSearch(IReadOnlyList<PlannedChunk> chunks, int defaultTop)
        : this(() => chunks, defaultTop)
    {
    }

    public Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(string domain, string query, int top, CancellationToken cancellationToken)
    {
        DomainValue.Require(domain, nameof(domain));
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query) || !_domains.Value.TryGetValue(domain, out var index))
        {
            return Task.FromResult<IReadOnlyList<KnowledgeChunk>>([]);
        }

        var size = Math.Clamp(top > 0 ? top : _defaultTop, 1, KnowledgeIndexSchema.MaxTop);
        var results = index.Search(Terms(query).Distinct(StringComparer.Ordinal).ToList(), size)
            .Select(c => new KnowledgeChunk(c.Id, c.Domain, c.Title, c.Content, c.SourceUrl, null))
            .ToList();
        return Task.FromResult<IReadOnlyList<KnowledgeChunk>>(results);
    }

    private static readonly string[] Suffixes = ["ations", "ation", "ings", "ing", "ed", "e"];

    /// <summary>
    /// Tokenizer of ADR 0005 section 3 plus a light suffix stem applied to documents and queries alike, so "prepare",
    /// "preparing" and "preparation" meet. Search-only: the scripted client's whole-token keyword matching is unaffected.
    /// </summary>
    internal static IEnumerable<string> Terms(string text) => KnowledgeTokenizer.Tokenize(text).Select(Stem);

    private static string Stem(string token)
    {
        foreach (var suffix in Suffixes)
        {
            if (token.Length >= suffix.Length + 4 && token.EndsWith(suffix, StringComparison.Ordinal))
            {
                return token[..^suffix.Length];
            }
        }

        return token;
    }

    private sealed class DomainIndex
    {
        private readonly List<(PlannedChunk Chunk, Dictionary<string, int> Tf, int Length)> _docs = [];
        private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);
        private readonly double _avgLength;

        public DomainIndex(List<PlannedChunk> chunks)
        {
            foreach (var chunk in chunks)
            {
                var tf = new Dictionary<string, int>(StringComparer.Ordinal);
                var length = 0;
                foreach (var token in Terms(chunk.Title))
                {
                    tf[token] = tf.GetValueOrDefault(token) + 2; // title boost
                    length += 2;
                }

                foreach (var token in Terms(chunk.Content))
                {
                    tf[token] = tf.GetValueOrDefault(token) + 1;
                    length++;
                }

                foreach (var term in tf.Keys)
                {
                    _df[term] = _df.GetValueOrDefault(term) + 1;
                }

                _docs.Add((chunk, tf, length));
            }

            _avgLength = _docs.Count == 0 ? 0 : _docs.Average(d => (double)d.Length);
        }

        public IEnumerable<PlannedChunk> Search(IReadOnlyList<string> terms, int size)
        {
            var n = _docs.Count;
            var scored = new List<(PlannedChunk Chunk, double Score)>();
            foreach (var (chunk, tf, length) in _docs)
            {
                var score = 0.0;
                foreach (var term in terms)
                {
                    if (!tf.TryGetValue(term, out var f))
                    {
                        continue;
                    }

                    var df = _df[term];
                    var idf = Math.Log(1 + ((n - df + 0.5) / (df + 0.5)));
                    var norm = _avgLength == 0 ? 1 : 1 - B + (B * length / _avgLength);
                    score += idf * (f * (K1 + 1)) / (f + (K1 * norm));
                }

                if (score > 0)
                {
                    scored.Add((chunk, score));
                }
            }

            return scored
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Chunk.Id, StringComparer.Ordinal)
                .Take(size)
                .Select(s => s.Chunk);
        }
    }
}
