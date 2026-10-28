using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.AI;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Ingestion.Tests;

public class IngestionRunnerTests
{
    /// <summary>Fake Azure Search: a sorted id set served with order-by/filter paging, recording deletes.</summary>
    private sealed class FakeSearchHandler(IEnumerable<string> existingIds) : HttpMessageHandler
    {
        private readonly List<string> _ids = existingIds.OrderBy(i => i, StringComparer.Ordinal).ToList();

        public List<string> Deleted { get; } = [];

        public List<string> Filters { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.Contains("search.post.search", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                var top = doc.RootElement.GetProperty("top").GetInt32();
                var filter = doc.RootElement.TryGetProperty("filter", out var f) ? f.GetString() : null;
                if (filter is not null)
                {
                    Filters.Add(filter);
                }

                var page = _ids.AsEnumerable();
                if (filter is not null)
                {
                    var last = filter[(filter.IndexOf('\'', StringComparison.Ordinal) + 1)..^1].Replace("''", "'", StringComparison.Ordinal);
                    page = page.Where(i => string.CompareOrdinal(i, last) > 0);
                }

                var value = page.Take(top).Select(i => new Dictionary<string, object> { ["@search.score"] = 1.0, ["id"] = i });
                return Json(new { value });
            }

            if (path.Contains("search.index", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                var results = new List<object>();
                foreach (var action in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    var key = action.GetProperty("id").GetString()!;
                    if (action.GetProperty("@search.action").GetString() == "delete")
                    {
                        Deleted.Add(key);
                    }

                    results.Add(new { key, status = true, statusCode = 200 });
                }

                return Json(new { value = results });
            }

            // Create/update index.
            return Json(new { name = "agentwf-knowledge", fields = Array.Empty<object>() });
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 0.1f, 0.2f })).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static IngestionRunner Runner(FakeSearchHandler handler, string source, out List<string> log)
    {
        var lines = new List<string>();
        log = lines;
        var transport = new HttpClientTransport(new HttpClient(handler));
        var clients = new IngestionClients(AzureAuthMode.Key, new Uri("https://svc.search.windows.net"), "agentwf-knowledge", "admin-key", () => throw new InvalidOperationException(), transport);
        var options = new IngestionOptions { SourcePath = source, VectorizerResourceUri = "https://aoai.openai.azure.com", EmbeddingDeployment = "emb" };
        return new IngestionRunner(options, "agentwf-knowledge", clients, new FakeEmbeddings(), lines.Add);
    }

    private static string MakeSource(bool withDoc)
    {
        var root = Path.Combine(Path.GetTempPath(), "ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (withDoc)
        {
            Directory.CreateDirectory(Path.Combine(root, "claims"));
            File.WriteAllText(Path.Combine(root, "claims", "a.md"), "# Claims\n\nSend the form.\n");
        }

        return root;
    }

    [Fact]
    public async Task Stale_chunks_beyond_one_page_are_all_deleted()
    {
        var source = MakeSource(withDoc: true);
        try
        {
            var stale = Enumerable.Range(0, 2500).Select(i => $"stale-{i:D5}").ToList();
            var handler = new FakeSearchHandler(stale);
            var summary = await Runner(handler, source, out _).RunAsync(dryRun: false, allowEmpty: false, CancellationToken.None);

            Assert.Equal(2500, summary.Deleted);
            Assert.Equal(stale.OrderBy(s => s, StringComparer.Ordinal), handler.Deleted.OrderBy(s => s, StringComparer.Ordinal));
            Assert.True(handler.Filters.Count >= 2, "multiple pages must be requested");
        }
        finally
        {
            Directory.Delete(source, true);
        }
    }

    [Fact]
    public async Task Ids_with_quotes_are_escaped_in_the_paging_filter()
    {
        var handler = new FakeSearchHandler(Enumerable.Range(0, 1001).Select(i => $"a'{i:D5}"));
        var transport = new HttpClientTransport(new HttpClient(handler));
        var search = new IngestionClients(AzureAuthMode.Key, new Uri("https://svc.search.windows.net"), "i", "k", () => throw new InvalidOperationException(), transport).CreateSearchClient();
        var ids = await IngestionRunner.ListIdsAsync(search, CancellationToken.None);
        Assert.Equal(1001, ids.Count);
        Assert.Contains("''", handler.Filters[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_source_with_existing_index_is_refused_without_allow_empty_and_deletes_with_it()
    {
        var source = MakeSource(withDoc: false);
        try
        {
            var ids = Enumerable.Range(0, 1500).Select(i => $"x-{i:D5}").ToList();

            var refused = new FakeSearchHandler(ids);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Runner(refused, source, out _).RunAsync(false, false, CancellationToken.None));
            Assert.Contains("1500", ex.Message, StringComparison.Ordinal);
            Assert.Empty(refused.Deleted);

            var allowed = new FakeSearchHandler(ids);
            var summary = await Runner(allowed, source, out _).RunAsync(false, true, CancellationToken.None);
            Assert.Equal(1500, summary.Deleted);
            Assert.Equal(1500, allowed.Deleted.Count);
        }
        finally
        {
            Directory.Delete(source, true);
        }
    }
}
