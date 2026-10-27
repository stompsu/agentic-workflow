namespace SupportChat.LocalHost.Knowledge;

/// <summary>
/// ADR 0005 section 3 tokenizer: lowercase, split on non letter/digit, drop a small English stop-word list, strip a
/// trailing plural "s" for tokens longer than 3. Applied identically to documents and queries.
/// </summary>
internal static class KnowledgeTokenizer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "do", "does", "for", "from", "how", "i", "if",
        "in", "into", "is", "it", "my", "of", "on", "or", "our", "should", "that", "the", "their", "then", "there",
        "this", "to", "was", "we", "what", "when", "which", "will", "with", "you", "your",
    };

    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isToken = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isToken && start < 0)
            {
                start = i;
            }
            else if (!isToken && start >= 0)
            {
                var token = text.AsSpan(start, i - start).ToString().ToLowerInvariant();
                start = -1;
                if (StopWords.Contains(token))
                {
                    continue;
                }

                if (token.Length > 3 && token[^1] == 's')
                {
                    token = token[..^1];
                }

                tokens.Add(token);
            }
        }

        return tokens;
    }
}
