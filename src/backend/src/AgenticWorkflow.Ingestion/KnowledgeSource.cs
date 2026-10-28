using AgenticWorkflow.Infrastructure.Search;

namespace AgenticWorkflow.Ingestion;

public sealed record SourceDocument(string SourcePath, string Domain, string Title, string? SourceUrl, string Markdown);

/// <summary>
/// Reads <c>&lt;root&gt;/&lt;domain&gt;/**/*.md</c>. The first folder name is the domain and must match the domain
/// pattern. Optional front matter (<c>---</c> block) may set <c>title</c> and <c>sourceUrl</c>; otherwise the title is
/// the first H1 or the file name.
/// </summary>
public static class KnowledgeSource
{
    public static IReadOnlyList<SourceDocument> Read(string root, Action<string>? warn = null)
    {
        var documents = new List<SourceDocument>();
        if (!Directory.Exists(root))
        {
            return documents;
        }

        foreach (var domainDir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var domain = Path.GetFileName(domainDir);
            if (!DomainValue.IsValid(domain))
            {
                warn?.Invoke($"Skipping folder '{domain}': the folder name must match ^[a-z0-9-]+$.");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(domainDir, "*.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                documents.Add(Parse(relative, domain, File.ReadAllText(file)));
            }
        }

        return documents;
    }

    public static SourceDocument Parse(string sourcePath, string domain, string raw)
    {
        var text = raw.Replace("\r\n", "\n");
        string? title = null;
        string? url = null;

        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in text[4..end].Split('\n'))
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon <= 0)
                    {
                        continue;
                    }

                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                    if (key.Equals("title", StringComparison.OrdinalIgnoreCase))
                    {
                        title = value;
                    }
                    else if (key.Equals("sourceUrl", StringComparison.OrdinalIgnoreCase))
                    {
                        url = value;
                    }
                }

                text = text[(end + 5)..];
            }
        }

        title ??= text.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim();
        title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(sourcePath) : title;

        // Only absolute http(s) links are kept (ADR 0002 section 9); anything else is dropped at the source.
        if (url is not null
            && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            url = null;
        }

        return new SourceDocument(sourcePath, domain, title, string.IsNullOrWhiteSpace(url) ? null : url, text);
    }
}
