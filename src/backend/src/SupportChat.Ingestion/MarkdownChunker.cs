using System.Text;

namespace SupportChat.Ingestion;

public sealed record Chunk(int Index, string Title, string Content);

/// <summary>
/// Deterministic markdown chunker: same input and options always give the same chunks (and therefore the same ids).
/// Splits at headings, packs paragraphs up to ~ChunkTokens (chars / 4), carries ~ChunkOverlapTokens of the previous
/// chunk into the next one within a section. Pure function; no I/O, no clock, no randomness.
/// </summary>
public static class MarkdownChunker
{
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    public static IReadOnlyList<Chunk> Chunk(string documentTitle, string markdown, int chunkTokens, int overlapTokens)
    {
        if (chunkTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkTokens));
        }

        var maxChars = chunkTokens * 4;
        var overlapChars = Math.Clamp(overlapTokens, 0, chunkTokens / 2) * 4;
        var chunks = new List<Chunk>();

        foreach (var (heading, body) in Sections(markdown.Replace("\r\n", "\n").Replace('\r', '\n')))
        {
            var title = heading is null ? documentTitle : $"{documentTitle} - {heading}";
            string? previousTail = null;
            foreach (var piece in Pack(Paragraphs(body), maxChars, overlapChars))
            {
                var text = previousTail is null ? piece : previousTail + "\n\n" + piece;
                chunks.Add(new Chunk(chunks.Count, title, text.Trim()));
                previousTail = Tail(piece, overlapChars);
            }
        }

        return chunks;
    }

    private static IEnumerable<(string? Heading, string Body)> Sections(string markdown)
    {
        string? heading = null;
        var body = new StringBuilder();
        var inFence = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence && IsHeading(line, out var text))
            {
                if (body.ToString().Trim().Length > 0)
                {
                    yield return (heading, body.ToString());
                }

                heading = text;
                body.Clear();
                continue;
            }

            body.Append(line).Append('\n');
        }

        if (body.ToString().Trim().Length > 0)
        {
            yield return (heading, body.ToString());
        }
    }

    private static bool IsHeading(string line, out string text)
    {
        text = string.Empty;
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is < 1 or > 6 || level >= line.Length || line[level] != ' ')
        {
            return false;
        }

        text = line[(level + 1)..].Trim();
        return text.Length > 0;
    }

    private static IEnumerable<string> Paragraphs(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0);

    private static IEnumerable<string> Pack(IEnumerable<string> paragraphs, int maxChars, int overlapChars)
    {
        // Leave room for the overlap that is prepended to every chunk except the first of a section.
        var budget = Math.Max(1, maxChars - overlapChars);
        var current = new StringBuilder();
        foreach (var paragraph in paragraphs.SelectMany(p => SplitLong(p, budget)))
        {
            if (current.Length > 0 && current.Length + 2 + paragraph.Length > budget)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(paragraph);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>Splits an over-long paragraph at word boundaries (hard cut when a single word is longer).</summary>
    private static IEnumerable<string> SplitLong(string paragraph, int max)
    {
        if (paragraph.Length <= max)
        {
            yield return paragraph;
            yield break;
        }

        var start = 0;
        while (start < paragraph.Length)
        {
            var end = Math.Min(start + max, paragraph.Length);
            if (end < paragraph.Length)
            {
                var space = paragraph.LastIndexOf(' ', end - 1, end - start);
                if (space > start)
                {
                    end = space;
                }
            }

            yield return paragraph[start..end].Trim();
            start = end;
            while (start < paragraph.Length && paragraph[start] == ' ')
            {
                start++;
            }
        }
    }

    private static string? Tail(string piece, int overlapChars)
    {
        if (overlapChars <= 0 || piece.Length <= overlapChars)
        {
            return overlapChars <= 0 ? null : piece;
        }

        var from = piece.Length - overlapChars;
        var space = piece.IndexOf(' ', from);
        return space < 0 ? piece[from..] : piece[(space + 1)..];
    }
}
