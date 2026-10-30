using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.AI;

namespace AgenticWorkflow.Ingestion.Tests;

/// <summary>The real client against a fake HTTP handler: the embeddings URL is what the unit tests with a fake generator cannot see.</summary>
public class EmbeddingClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            const string json = """
                {"object":"list","model":"m","data":[{"object":"embedding","index":0,"embedding":[0.5,0.25]}],"usage":{"prompt_tokens":1,"total_tokens":1}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    [Theory]
    [InlineData("https://res.openai.azure.com")]
    [InlineData("https://res.openai.azure.com/")]
    public async Task Calls_the_resource_v1_endpoint_with_a_bearer_token_and_the_deployment_name(string resource)
    {
        var handler = new CapturingHandler();
        var credential = new FakeCredential();
        using var http = new HttpClient(handler);
        var generator = IngestionClients.CreateEmbeddingGenerator(new Uri(resource), "embedding", credential, new HttpClientPipelineTransport(http));

        var result = await generator.GenerateAsync(["hello"]);

        Assert.Equal(new[] { 0.5f, 0.25f }, result[0].Vector.ToArray());
        // Never the project endpoint (/api/projects/...), which does not route embeddings.
        Assert.Equal("https://res.openai.azure.com/openai/v1/embeddings", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("fake-token", handler.Request.Headers.Authorization.Parameter);
        Assert.Equal(["https://ai.azure.com/.default"], credential.Scopes);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("embedding", body.RootElement.GetProperty("model").GetString());
    }
}
