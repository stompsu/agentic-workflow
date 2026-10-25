#pragma warning disable OPENAI001
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Infrastructure.Models;

namespace SupportChat.Infrastructure.Tests.Models;

/// <summary>D3: every model call carries "store": false. Requests are captured by a fake HttpClient transport (no network).</summary>
public class ChatClientStoreDisabledTests
{
    private const string ResponseJson = """
        {"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"gpt-test","store":false,
         "output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant",
         "content":[{"type":"output_text","text":"hi","annotations":[]}]}],
         "usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
        """;

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            var streaming = body.Contains("\"stream\":true", StringComparison.Ordinal);
            var json = streaming
                ? "event: response.completed\ndata: " + JsonSerializer.Serialize(new { type = "response.completed", sequence_number = 1, response = JsonDocument.Parse(ResponseJson).RootElement }) + "\n\n"
                : ResponseJson;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, streaming ? "text/event-stream" : "application/json"),
            };
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("t", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private static (IChatClient Client, CapturingHandler Handler) Build()
    {
        var handler = new CapturingHandler();
        var factory = new ChatClientFactory(
            Options.Create(new FoundryOptions { ProjectEndpoint = "https://example.invalid/api/projects/p" }),
            new StubCredentialProvider(new ThrowingCredential()),
            (uri, _) => new AIProjectClient(uri, new FakeCredential(), new AIProjectClientOptions
            {
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = new ClientRetryPolicy(0),
            }));
        return (factory.Create("gpt-test"), handler);
    }

    private static ChatOptions[] CallerOptions() =>
    [
        new ChatOptions(),
        new ChatOptions { Temperature = 0.2f, Instructions = "be brief", RawRepresentationFactory = _ => new OpenAI.Responses.CreateResponseOptions() },
        // A caller that explicitly asks for storage must not win.
        new ChatOptions { RawRepresentationFactory = _ => new OpenAI.Responses.CreateResponseOptions { StoredOutputEnabled = true } },
    ];

    private static void AssertStoreFalse(CapturingHandler handler, int expected)
    {
        Assert.Equal(expected, handler.Requests.Count);
        foreach (var (path, body) in handler.Requests)
        {
            Assert.EndsWith("/responses", path);
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("store", out var store), body);
            Assert.Equal(JsonValueKind.False, store.ValueKind);
        }
    }

    [Fact]
    public async Task Non_streaming_request_sends_store_false_for_every_caller_options_shape()
    {
        foreach (var options in CallerOptions())
        {
            var (client, handler) = Build();
            await client.GetResponseAsync("hello", options);
            AssertStoreFalse(handler, 1);
        }
    }

    [Fact]
    public async Task Streaming_request_sends_store_false_for_every_caller_options_shape()
    {
        foreach (var options in CallerOptions())
        {
            var (client, handler) = Build();
            await foreach (var _ in client.GetStreamingResponseAsync("hello", options)) { }
            AssertStoreFalse(handler, 1);
        }
    }

    [Fact]
    public async Task Null_options_also_send_store_false()
    {
        var (client, handler) = Build();
        await client.GetResponseAsync("hello");
        AssertStoreFalse(handler, 1);
    }
}
