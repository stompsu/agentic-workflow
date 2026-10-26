using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using SupportChat.Agents.Knowledge;
using SupportChat.Agents.Prompts;
using SupportChat.Agents.Workflows.Echo;
using SupportChat.Agents.Workflows.SupportTicket;
using SupportChat.Core.Knowledge;
using SupportChat.Core.Models;
using SupportChat.Core.Prompts;
using SupportChat.Core.Workflows;
using static SupportChat.Agents.Tests.SupportTicketHarness;

namespace SupportChat.Agents.Tests;

public class EchoWorkflowTests
{
    [Fact]
    public async Task Streams_the_input_back_word_by_word_through_the_protocol()
    {
        var events = await Collect(new EchoWorkflow(), Request("hello brave  new world"));

        AssertProtocol(events);
        var tokens = events.OfType<TokenDelta>().Select(t => t.Text).ToArray();
        Assert.Equal(4, tokens.Length);
        Assert.Equal("hello brave  new world", string.Concat(tokens));
        Assert.Equal("Echo", Assert.IsType<AgentChanged>(events[0]).Name);
    }

    [Fact]
    public async Task Empty_message_still_produces_a_token()
    {
        var events = await Collect(new EchoWorkflow(), Request("   "));

        AssertProtocol(events);
    }

    [Fact]
    public async Task Honours_cancellation_between_words()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in new EchoWorkflow().RunAsync(Request("a b c d e"), cts.Token))
            {
                if (e is TokenDelta)
                {
                    await cts.CancelAsync();
                }
            }
        });
    }
}

public class PromptProviderTests
{
    private static readonly string[] Expected =
    [
        "Workflows/SupportTicket/Prompts/orchestrator.v1.md",
        "Workflows/SupportTicket/Prompts/policy.v1.md",
        "Workflows/SupportTicket/Prompts/claims.v1.md",
        "Workflows/SupportTicket/Prompts/health-assessments.v1.md",
        "Workflows/SupportTicket/Prompts/clarification-fallback.v1.md",
    ];

    [Fact]
    public void Loads_every_embedded_prompt_and_the_expected_ones_exist()
    {
        var provider = new EmbeddedPromptProvider();

        Assert.NotEmpty(provider.Paths);
        foreach (var path in provider.Paths)
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.Get(path)), path);
        }

        foreach (var expected in Expected)
        {
            Assert.Contains(expected, provider.Paths);
        }
    }

    [Fact]
    public void Every_prompt_file_name_carries_a_version()
    {
        Assert.All(new EmbeddedPromptProvider().Paths, p => Assert.Matches(@"\.v\d+\.md$", p));
    }

    [Fact]
    public void Missing_prompt_throws()
    {
        Assert.Throws<FileNotFoundException>(() => new EmbeddedPromptProvider().Get("Workflows/Nope/x.v1.md"));
    }

    [Fact]
    public void Backslash_paths_are_normalized()
    {
        Assert.NotEmpty(new EmbeddedPromptProvider().Get("Workflows\\SupportTicket\\Prompts\\policy.v1.md"));
    }

    [Fact]
    public void Specialist_prompts_state_the_grounding_and_injection_rules()
    {
        var p = new EmbeddedPromptProvider();
        foreach (var f in new[] { "policy", "claims", "health-assessments" })
        {
            var text = p.Get($"Workflows/SupportTicket/Prompts/{f}.v1.md");
            Assert.Contains("search_knowledge", text);
            Assert.Contains("[S1]", text);
            Assert.Contains("untrusted", text);
            Assert.Contains("NO_RESULTS", text);
        }
    }
}

public class KnowledgeUnitTests
{
    [Fact]
    public void Collector_dedupes_the_same_chunk_and_numbers_from_one()
    {
        var c = new CitationCollector();
        var a = c.Register(new KnowledgeChunk("a", "policy", "A", "x", null, null));
        var b = c.Register(new KnowledgeChunk("b", "policy", "B", "x", null, null));
        var a2 = c.Register(new KnowledgeChunk("a", "policy", "A", "x", null, null));

        Assert.Equal(new[] { "S1", "S2", "S1" }, new[] { a, b, a2 });
        Assert.Equal(2, c.Count);
    }

    [Theory]
    [InlineData("a & b < c > d", "a &amp; b &lt; c &gt; d")]
    [InlineData("&lt;", "&amp;lt;")]
    [InlineData("quotes \" ' stay", "quotes \" ' stay")]
    public void Encodes_only_amp_lt_gt(string input, string expected) =>
        Assert.Equal(expected, KnowledgeSanitizer.EncodeForModel(input));

    [Fact]
    public void Removes_control_and_bidi_characters()
    {
        Assert.Equal("ab", KnowledgeSanitizer.RemoveControlCharacters("a\u0000\u0007‮​b"));
        Assert.Equal("a\nb\tc", KnowledgeSanitizer.RemoveControlCharacters("a\nb\r\tc\u0001", keepLineBreaks: true));
    }

    [Fact]
    public void Tool_rejects_a_domain_that_is_not_a_slug()
    {
        Assert.Throws<ArgumentException>(() => KnowledgeSearchTool.Create(
            new FakeKnowledgeSearch(), "policy' or 1 eq 1", new CitationCollector(), 5, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }
}

public class RegistrationTests
{
    private sealed class ThrowawayWorkflow : IWorkflowDefinition
    {
        public string Id => "throwaway";

        public string DisplayName => "Throwaway";

        public string Description => "test";

        public async IAsyncEnumerable<ChatStreamEvent> RunAsync(WorkflowRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new AgentChanged("x");
            yield return new TokenDelta("y");
            yield return new TurnCompleted();
        }
    }

    private static ServiceProvider Build(Dictionary<string, string?> config, Action<IServiceCollection>? extra = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
        services.AddSingleton<IKnowledgeSearch>(new FakeKnowledgeSearch());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SupportChat.Core.Options.SearchServiceOptions()));
        services.AddAgentWorkflows(cfg);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Agents() => new()
    {
        ["Workflows:SupportTicket:Agents:Orchestrator:Deployment"] = "o",
        ["Workflows:SupportTicket:Agents:Policy:Deployment"] = "p",
        ["Workflows:SupportTicket:Agents:Claims:Deployment"] = "c",
        ["Workflows:SupportTicket:Agents:HealthAssessments:Deployment"] = "h",
        ["Workflows:SupportTicket:ConfidenceThreshold"] = "0.8",
    };

    [Fact]
    public void Echo_is_not_registered_by_default()
    {
        using var sp = Build(Agents());
        var ids = sp.GetRequiredService<IWorkflowRegistry>().List().Select(d => d.Id).ToArray();

        Assert.Equal(new[] { "support-ticket" }, ids);
    }

    [Fact]
    public void Echo_is_registered_only_when_enabled()
    {
        var config = Agents();
        config["Workflows:Echo:Enabled"] = "true";
        using var sp = Build(config);
        var ids = sp.GetRequiredService<IWorkflowRegistry>().List().Select(d => d.Id).ToArray();

        Assert.Equal(new[] { "support-ticket", "echo" }, ids);
    }

    [Fact]
    public void Options_bind_from_configuration()
    {
        using var sp = Build(Agents());
        var o = sp.GetRequiredService<IOptions<SupportTicketOptions>>().Value;

        Assert.Equal(0.8, o.ConfidenceThreshold);
        Assert.Equal(2, o.MaxConsecutiveClarifications);
        Assert.Equal("h", o.Agents["HealthAssessments"].Deployment);
    }

    [Fact]
    public void A_new_workflow_type_needs_one_registration_line()
    {
        using var sp = Build(Agents(), s => s.AddWorkflow<ThrowawayWorkflow>());
        var registry = sp.GetRequiredService<IWorkflowRegistry>();

        Assert.Contains(registry.List(), d => d.Id == "throwaway" && d.DisplayName == "Throwaway");
        Assert.True(registry.TryGet("throwaway", out var wf));
        Assert.IsType<ThrowawayWorkflow>(wf);
    }

    [Fact]
    public void Validator_reports_missing_deployments_and_bad_threshold()
    {
        var options = new SupportTicketOptions { ConfidenceThreshold = 2 };
        options.Agents["Policy"] = new() { Deployment = "p" };

        var result = new SupportTicketOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ConfidenceThreshold"));
        Assert.Contains(result.Failures!, f => f.Contains("Orchestrator:Deployment"));
        Assert.Contains(result.Failures!, f => f.Contains("Claims:Deployment"));
        Assert.DoesNotContain(result.Failures!, f => f.Contains("Policy:Deployment"));
    }

    [Theory]
    [InlineData("Orchestrator", "<chat-deployment>")]
    [InlineData("Orchestrator", "REPLACE_ME")]
    [InlineData("Orchestrator", "replace_me-x")]
    [InlineData("Policy", "<chat-deployment>")]
    [InlineData("Policy", "REPLACE_ME")]
    [InlineData("Policy", "replace_me-x")]
    [InlineData("Claims", "<chat-deployment>")]
    [InlineData("Claims", "REPLACE_ME")]
    [InlineData("Claims", "replace_me-x")]
    [InlineData("HealthAssessments", "<chat-deployment>")]
    [InlineData("HealthAssessments", "REPLACE_ME")]
    [InlineData("HealthAssessments", "replace_me-x")]
    public void Validator_rejects_placeholder_deployment(string key, string value)
    {
        var options = new SupportTicketOptions();
        foreach (var k in Workflows.SupportTicket.SupportTicketWorkflow.AgentKeys)
        {
            options.Agents[k] = new() { Deployment = k == key ? value : "gpt-ok" };
        }

        var result = new SupportTicketOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains($"{key}:Deployment", failure);
        Assert.DoesNotContain(value, failure);
    }

    [Fact]
    public void Validator_accepts_real_deployment_names()
    {
        var options = new SupportTicketOptions();
        foreach (var k in Workflows.SupportTicket.SupportTicketWorkflow.AgentKeys)
        {
            options.Agents[k] = new() { Deployment = "gpt-4o-mini" };
        }

        Assert.True(new SupportTicketOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void Validator_bounds_MaxToolCallsPerTurn(int value, bool fails)
    {
        var options = new SupportTicketOptions { MaxToolCallsPerTurn = value };
        foreach (var key in Workflows.SupportTicket.SupportTicketWorkflow.AgentKeys)
        {
            options.Agents[key] = new() { Deployment = "d" };
        }

        var result = new SupportTicketOptionsValidator().Validate(null, options);

        Assert.Equal(fails, result.Failed);
    }
}
