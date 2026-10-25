using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Infrastructure.Search;

namespace SupportChat.Infrastructure.Tests.Search;

public class AzureSearchKnowledgeSearchTests
{
    private sealed class RecordingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, JsonNode? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, text is null ? null : JsonNode.Parse(text)));
            return new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private static (AzureSearchKnowledgeSearch Sut, RecordingHandler Handler) Create(string responseJson, SearchServiceOptions? options = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(responseJson, status);
        var clientOptions = new SearchClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        clientOptions.Retry.MaxRetries = 0;
        var client = new SearchClient(new Uri("https://fake.search.windows.net"), "support-knowledge", new AzureKeyCredential("k"), clientOptions);
        return (new AzureSearchKnowledgeSearch(client, Options.Create(options ?? new SearchServiceOptions()), NullLogger<AzureSearchKnowledgeSearch>.Instance), handler);
    }

    private const string TwoResults = """
        {"value":[
          {"@search.score":1.2,"@search.rerankerScore":3.1,"id":"a1","domain":"claims","title":"Claims times","content":"Ten working days.","sourceUrl":"https://kb.example.com/claims"},
          {"@search.score":0.9,"@search.rerankerScore":0.4,"id":"a2","domain":"claims","title":"Weak","content":"Irrelevant.","sourceUrl":""}
        ]}
        """;

    [Fact]
    public async Task Sends_a_hybrid_semantic_query_with_the_validated_domain_filter()
    {
        var (sut, handler) = Create(TwoResults);

        await sut.SearchAsync("claims", "how long does a claim take", 3, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("/indexes('support-knowledge')/docs/search.post.search", request.Uri.AbsolutePath);
        var body = request.Body!.AsObject();
        Assert.Equal("how long does a claim take", body["search"]!.GetValue<string>());
        Assert.Equal("domain eq 'claims'", body["filter"]!.GetValue<string>());
        Assert.Equal("semantic", body["queryType"]!.GetValue<string>());
        Assert.Equal("semantic-default", body["semanticConfiguration"]!.GetValue<string>());
        Assert.Equal(3, body["top"]!.GetValue<int>());
        Assert.Equal("id,domain,title,content,sourceUrl", body["select"]!.GetValue<string>());

        var vector = Assert.Single(body["vectorQueries"]!.AsArray())!.AsObject();
        Assert.Equal("text", vector["kind"]!.GetValue<string>());
        Assert.Equal("how long does a claim take", vector["text"]!.GetValue<string>());
        Assert.Equal("contentVector", vector["fields"]!.GetValue<string>());
        Assert.Equal(50, vector["k"]!.GetValue<int>());
    }

    [Fact]
    public async Task Maps_chunks_and_drops_results_below_the_reranker_threshold()
    {
        var (sut, _) = Create(TwoResults, new SearchServiceOptions { MinRerankerScore = 1.5 });

        var chunks = await sut.SearchAsync("claims", "q", 5, CancellationToken.None);

        var chunk = Assert.Single(chunks);
        Assert.Equal("a1", chunk.Id);
        Assert.Equal("claims", chunk.Domain);
        Assert.Equal("Claims times", chunk.Title);
        Assert.Equal("Ten working days.", chunk.Content);
        Assert.Equal("https://kb.example.com/claims", chunk.SourceUrl);
        Assert.Equal(3.1, chunk.RerankerScore);
    }

    [Fact]
    public async Task Empty_source_url_becomes_null()
    {
        var (sut, _) = Create(TwoResults, new SearchServiceOptions { MinRerankerScore = 0 });

        var chunks = await sut.SearchAsync("claims", "q", 5, CancellationToken.None);

        Assert.Equal(2, chunks.Count);
        Assert.Null(chunks[1].SourceUrl);
    }

    [Fact]
    public async Task Non_positive_top_falls_back_to_configured_TopK_and_large_top_is_capped()
    {
        var (sut, handler) = Create("""{"value":[]}""", new SearchServiceOptions { TopK = 7 });

        await sut.SearchAsync("policy", "q", 0, CancellationToken.None);
        await sut.SearchAsync("policy", "q", 10_000, CancellationToken.None);

        Assert.Equal(7, handler.Requests[0].Body!["top"]!.GetValue<int>());
        Assert.Equal(50, handler.Requests[1].Body!["top"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Claims")]
    [InlineData("claims' or domain ne '")]
    [InlineData("claims or 1 eq 1")]
    [InlineData("a_b")]
    [InlineData("claims\n")]
    public async Task Invalid_domains_are_rejected_before_any_request(string domain)
    {
        var (sut, handler) = Create("""{"value":[]}""");

        await Assert.ThrowsAsync<ArgumentException>(() => sut.SearchAsync(domain, "q", 5, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Blank_query_returns_nothing_without_a_request()
    {
        var (sut, handler) = Create("""{"value":[]}""");

        Assert.Empty(await sut.SearchAsync("claims", "   ", 5, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Search_failures_propagate_so_the_tool_can_report_SEARCH_UNAVAILABLE()
    {
        var (sut, _) = Create("""{"error":{"code":"x","message":"boom"}}""", status: HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<RequestFailedException>(() => sut.SearchAsync("claims", "q", 5, CancellationToken.None));
    }

    [Theory]
    [InlineData("claims", true)]
    [InlineData("health-2", true)]
    [InlineData("", false)]
    [InlineData("Policy", false)]
    [InlineData("a b", false)]
    [InlineData("it's", false)]
    public void DomainValue_matches_the_documented_pattern(string value, bool valid) =>
        Assert.Equal(valid, DomainValue.IsValid(value));
}
