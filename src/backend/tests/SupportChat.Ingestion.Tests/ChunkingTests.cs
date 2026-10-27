using SupportChat.Infrastructure.Search.Source;
using SupportChat.Ingestion;

namespace SupportChat.Ingestion.Tests;

public class ChunkingTests
{
    private static string Para(string word, int words) => string.Join(' ', Enumerable.Repeat(word, words));

    private const string Doc = """
        # Claims handling

        Intro paragraph about claims.

        ## Timing

        Most claims are settled within ten working days.

        Complex claims may take longer.

        ## Documents

        Send the form and the receipts.
        """;

    [Fact]
    public void Chunking_is_deterministic()
    {
        var a = MarkdownChunker.Chunk("Claims", Doc, 800, 80);
        var b = MarkdownChunker.Chunk("Claims", Doc, 800, 80);
        Assert.Equal(a, b);
        Assert.Equal(a.Select(c => ChunkIds.For("claims/handling.md", c.Index)), b.Select(c => ChunkIds.For("claims/handling.md", c.Index)));
    }

    [Fact]
    public void Headings_start_new_chunks_and_extend_the_title()
    {
        var chunks = MarkdownChunker.Chunk("Claims", Doc, 800, 80);
        Assert.Equal(["Claims - Claims handling", "Claims - Timing", "Claims - Documents"], chunks.Select(c => c.Title));
        Assert.Equal([0, 1, 2], chunks.Select(c => c.Index));
        Assert.Contains("ten working days", chunks[1].Content);
        Assert.DoesNotContain("Send the form", chunks[1].Content);
    }

    [Fact]
    public void Headings_inside_code_fences_are_not_split_points()
    {
        var chunks = MarkdownChunker.Chunk("T", "Text\n\n```\n# not a heading\n```\n\nMore", 800, 0);
        Assert.Single(chunks);
    }

    [Fact]
    public void Long_sections_are_split_to_the_token_budget_with_overlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"P{i} " + Para("word", 60)));
        var chunks = MarkdownChunker.Chunk("T", text, 200, 40); // ~800 chars per chunk

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(MarkdownChunker.EstimateTokens(c.Content) <= 200, $"chunk {c.Index} too big"));
        // Overlap: the end of chunk n reappears at the start of chunk n+1.
        for (var i = 0; i + 1 < chunks.Count; i++)
        {
            var tailWord = chunks[i].Content.Split(' ', '\n')[^1];
            Assert.Contains(tailWord, chunks[i + 1].Content[..Math.Min(200, chunks[i + 1].Content.Length)]);
        }
    }

    [Fact]
    public void A_single_giant_word_run_is_still_cut()
    {
        var chunks = MarkdownChunker.Chunk("T", new string('x', 5000), 100, 0);
        Assert.True(chunks.Count >= 12);
        Assert.All(chunks, c => Assert.True(c.Content.Length <= 400));
    }

    [Fact]
    public void Empty_or_whitespace_documents_have_no_chunks() =>
        Assert.Empty(MarkdownChunker.Chunk("T", " \n\n  \n", 800, 80));

    [Fact]
    public void Line_endings_do_not_change_chunks() =>
        Assert.Equal(MarkdownChunker.Chunk("T", Doc, 800, 80), MarkdownChunker.Chunk("T", Doc.Replace("\n", "\r\n"), 800, 80));
}

public class ChunkIdTests
{
    [Fact]
    public void Id_is_base64url_sha256_of_path_hash_index()
    {
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("claims/a.md#3"u8))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, ChunkIds.For("claims/a.md", 3));
        Assert.Equal(43, expected.Length);
    }

    [Fact]
    public void Ids_are_stable_unique_and_url_safe()
    {
        var ids = Enumerable.Range(0, 500).Select(i => ChunkIds.For($"policy/doc{i % 7}.md", i)).ToList();
        Assert.Equal(ids, Enumerable.Range(0, 500).Select(i => ChunkIds.For($"policy/doc{i % 7}.md", i)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[A-Za-z0-9_-]+$", id));
        Assert.NotEqual(ChunkIds.For("a.md", 0), ChunkIds.For("a.md", 1));
        Assert.NotEqual(ChunkIds.For("a.md", 1), ChunkIds.For("b.md", 1));
    }
}

public class SourceAndPlanTests
{
    [Fact]
    public void Front_matter_sets_title_and_http_url_only()
    {
        var doc = KnowledgeSource.Parse("claims/a.md", "claims", "---\ntitle: \"Handling times\"\nsourceUrl: https://kb.example.com/x\n---\n# Ignored H1\n\nBody");
        Assert.Equal("Handling times", doc.Title);
        Assert.Equal("https://kb.example.com/x", doc.SourceUrl);
        Assert.DoesNotContain("sourceUrl", doc.Markdown);

        Assert.Null(KnowledgeSource.Parse("claims/a.md", "claims", "---\nsourceUrl: javascript:alert(1)\n---\nBody").SourceUrl);
    }

    [Fact]
    public void Title_falls_back_to_first_h1_then_file_name()
    {
        Assert.Equal("Heading", KnowledgeSource.Parse("p/a.md", "p", "# Heading\n\nx").Title);
        Assert.Equal("file-name", KnowledgeSource.Parse("p/file-name.md", "p", "just text").Title);
    }

    [Fact]
    public void Reading_a_folder_uses_the_first_level_as_domain_skips_invalid_names_and_uses_relative_posix_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ingest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "claims", "sub"));
            Directory.CreateDirectory(Path.Combine(root, "Bad Domain"));
            File.WriteAllText(Path.Combine(root, "claims", "sub", "a.md"), "# A\n\ntext");
            File.WriteAllText(Path.Combine(root, "claims", "ignore.txt"), "x");
            File.WriteAllText(Path.Combine(root, "Bad Domain", "b.md"), "text");
            var warnings = new List<string>();

            var docs = KnowledgeSource.Read(root, warnings.Add);

            var doc = Assert.Single(docs);
            Assert.Equal("claims", doc.Domain);
            Assert.Equal("claims/sub/a.md", doc.SourcePath);
            Assert.Single(warnings);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Plan_ids_are_the_same_across_runs_and_only_depend_on_path_and_index()
    {
        var docs = new[] { KnowledgeSource.Parse("claims/a.md", "claims", "# A\n\nx\n\n## B\n\ny") };
        var first = IngestionPlan.Build(docs, 800, 80);
        var second = IngestionPlan.Build(docs, 800, 80);
        Assert.Equal(first, second);
        Assert.Equal([ChunkIds.For("claims/a.md", 0), ChunkIds.For("claims/a.md", 1)], first.Select(p => p.Id));
        Assert.All(first, p => Assert.Equal("claims", p.Domain));
    }

    [Fact]
    public async Task Dry_run_reads_and_chunks_without_any_client()
    {
        var root = Path.Combine(Path.GetTempPath(), "ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "policy"));
        File.WriteAllText(Path.Combine(root, "policy", "a.md"), "# A\n\ntext");
        try
        {
            var lines = new List<string>();
            var runner = new IngestionRunner(new IngestionOptions { SourcePath = root }, "idx", null, null, lines.Add);
            var summary = await runner.RunAsync(dryRun: true, allowEmpty: false, CancellationToken.None);
            Assert.Equal(new IngestionSummary(1, 1, 0, 0), summary);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Index_definition_matches_data_model_section_2()
    {
        var options = new IngestionOptions { VectorizerResourceUri = "https://res.openai.azure.com", EmbeddingDeployment = "emb" };
        var index = IndexDefinition.Build("support-knowledge", options);

        Assert.Equal(["id", "domain", "title", "content", "contentVector", "sourceUrl"], index.Fields.Select(f => f.Name));
        Assert.True(index.Fields.Single(f => f.Name == "id").IsKey);
        Assert.True(index.Fields.Single(f => f.Name == "domain").IsFilterable);
        var vector = index.Fields.Single(f => f.Name == "contentVector");
        Assert.Equal(1536, vector.VectorSearchDimensions);
        Assert.Equal("vector-profile", vector.VectorSearchProfileName);
        Assert.True(vector.IsHidden);
        Assert.Equal("vector-profile", Assert.Single(index.VectorSearch.Profiles).Name);
        Assert.Equal("aoai-vectorizer", Assert.Single(index.VectorSearch.Profiles).VectorizerName);
        Assert.Equal("semantic-default", index.SemanticSearch.DefaultConfigurationName);
    }
}
