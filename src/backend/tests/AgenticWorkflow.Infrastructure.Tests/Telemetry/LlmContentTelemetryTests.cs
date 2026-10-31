#pragma warning disable OPENAI001
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Infrastructure.Models;
using AgenticWorkflow.Infrastructure.Telemetry;

namespace AgenticWorkflow.Infrastructure.Tests.Telemetry;

/// <summary>plan.md D14: what is captured when on, nothing when off. The model is a fake HTTP handler (no network).</summary>
public class LlmContentTelemetryTests
{
    private const string ResponseJson = """
        {"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"gpt-test","store":false,
         "output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant",
         "content":[{"type":"output_text","text":"the-model-answer","annotations":[]}]}],
         "usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
        """;

    private sealed class ModelHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
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

    private sealed class StubCredentials : AgenticWorkflow.Infrastructure.Azure.IAzureCredentialProvider
    {
        public TokenCredential Credential { get; } = new FakeCredential();
    }

    private sealed class CapturingLogger : ILogger, ILoggerProvider
    {
        public List<IReadOnlyDictionary<string, object?>> Records { get; } = [];

        public string? Category { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Records.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary());

        public ILogger CreateLogger(string categoryName)
        {
            Category = categoryName;
            return this;
        }

        public void Dispose()
        {
        }
    }

    private static (IChatClient Client, ModelHandler Handler) Factory(bool capture)
    {
        var handler = new ModelHandler();
        var factory = new ChatClientFactory(
            Options.Create(new FoundryOptions { ProjectEndpoint = "https://example.invalid/api/projects/p" }),
            new StubCredentials(),
            (uri, _) => new AIProjectClient(uri, new FakeCredential(), new AIProjectClientOptions
            {
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = new ClientRetryPolicy(0),
            }),
            capture);
        return (factory.Create("gpt-test"), handler);
    }

    /// <summary>Spans of this test only (tests run in parallel; the marker is unique per test).</summary>
    private static (ActivityListener Listener, List<Activity> Spans) Listen(string marker)
    {
        var spans = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LlmContentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if ((activity.GetTagItem("gen_ai.input.messages") as string)?.Contains(marker, StringComparison.Ordinal) == true)
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, spans);
    }

    private static ChatOptions Instructions() => new() { Instructions = "the-system-instructions" };

    [Fact]
    public async Task Captures_instructions_prompt_and_response_on_a_model_call()
    {
        var marker = Guid.NewGuid().ToString();
        var (listener, spans) = Listen(marker);
        using (listener)
        {
            var (client, _) = Factory(capture: true);
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, marker)], Instructions());
        }

        var span = Assert.Single(spans);
        Assert.Contains("the-system-instructions", (string)span.GetTagItem("gen_ai.system_instructions")!, StringComparison.Ordinal);
        Assert.Contains(marker, (string)span.GetTagItem("gen_ai.input.messages")!, StringComparison.Ordinal);
        Assert.Contains("the-model-answer", (string)span.GetTagItem("gen_ai.output.messages")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Captures_streaming_calls_and_keeps_store_false()
    {
        var marker = Guid.NewGuid().ToString();
        var (listener, spans) = Listen(marker);
        ModelHandler handler;
        using (listener)
        {
            var (client, modelHandler) = Factory(capture: true);
            handler = modelHandler;
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, marker)], Instructions()))
            {
            }
        }

        Assert.Single(spans);
        Assert.Contains("\"store\":false", Assert.Single(handler.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_nothing_when_off()
    {
        var marker = Guid.NewGuid().ToString();
        var (listener, spans) = Listen(marker);
        using (listener)
        {
            var (client, _) = Factory(capture: false);
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, marker)], Instructions());
        }

        Assert.Empty(spans);
    }

    [Fact]
    public void Long_content_is_logged_in_parts_that_rebuild_it_exactly()
    {
        var content = string.Concat(Enumerable.Range(0, 3000).Select(i => $"chunk-{i};"));
        var logger = new CapturingLogger();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logger));
        using var source = new ActivitySource(LlmContentTelemetry.SourceName);
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => ReferenceEquals(s, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var span = source.StartActivity("chat gpt-test")!;
        span.SetTag("gen_ai.input.messages", content);
        span.SetTag("gen_ai.request.model", "gpt-test");

        new LlmContentLogProcessor(factory).OnEnd(span);

        Assert.Equal(LlmContentTelemetry.LogCategory, logger.Category);
        Assert.True(logger.Records.Count > 1);
        Assert.All(logger.Records, r => Assert.True(((string)r["Content"]!).Length <= 8192));
        Assert.Equal(content, string.Concat(logger.Records.Select(r => (string)r["Content"]!)));
        Assert.Equal("gpt-test", logger.Records[0]["Deployment"]);
    }

    [Fact]
    public void Split_never_cuts_a_surrogate_pair()
    {
        var content = new string('a', LlmContentLogProcessor.MaxPartChars - 1) + "😀" + "b";

        var parts = LlmContentLogProcessor.Split(content);

        Assert.Equal(content, string.Concat(parts));
        Assert.All(parts, p => Assert.False(char.IsHighSurrogate(p[^1])));
    }

    [Fact]
    public void Other_sources_are_ignored()
    {
        var logger = new CapturingLogger();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logger));
        using var source = new ActivitySource("Something.Else");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => ReferenceEquals(s, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var span = source.StartActivity("x")!;
        span.SetTag("gen_ai.input.messages", "secret");

        new LlmContentLogProcessor(factory).OnEnd(span);

        Assert.Empty(logger.Records);
    }

    private static ServiceProvider Services(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:LlmContentEnabled"] = enabled ? "true" : "false",
            [LlmContentTelemetry.ConnectionStringKey] = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/",
        }).Build();
        return new ServiceCollection().AddLogging().AddLlmContentTelemetry(configuration).BuildServiceProvider();
    }

    [Fact]
    public void Exporters_build_when_on()
    {
        using var services = Services(enabled: true);

        Assert.NotNull(services.GetRequiredService<TracerProvider>());
    }

    [Fact]
    public void No_OpenTelemetry_is_registered_when_off()
    {
        using var services = Services(enabled: false);

        Assert.Null(services.GetService<TracerProvider>());
    }
}
