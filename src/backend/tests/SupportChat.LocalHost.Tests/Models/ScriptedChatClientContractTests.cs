using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SupportChat.Agents.Knowledge;
using SupportChat.Agents.Prompts;
using SupportChat.LocalHost.Models;
using SupportChat.LocalHost.Tests.Workflows;

namespace SupportChat.LocalHost.Tests.Models;

/// <summary>
/// Pins the two implicit contracts ScriptedChatClient depends on (ADR 0005 section 4): the JSON shape of the real
/// search_knowledge tool and the last_category sentence of orchestrator.v1.md.
/// </summary>
public class ScriptedChatClientContractTests
{
    private const string OrchestratorPrompt = "Workflows/SupportTicket/Prompts/orchestrator.v1.md";
    private const string Placeholder = "{{last_category}}";

    [Fact]
    public async Task Tool_json_has_the_shape_the_client_parses()
    {
        var json = await InvokeTool("claims", "file a claim");

        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");
        Assert.True(results.GetArrayLength() > 0);
        foreach (var item in results.EnumerateArray())
        {
            Assert.Matches(@"^S[1-9][0-9]*$", item.GetProperty("ref").GetString());
            Assert.Equal(JsonValueKind.String, item.GetProperty("title").ValueKind);
            Assert.StartsWith("<source ref=\"" + item.GetProperty("ref").GetString() + "\">", item.GetProperty("content").GetString());
            Assert.EndsWith("</source>", item.GetProperty("content").GetString());
        }
    }

    [Fact]
    public async Task Empty_result_has_the_note_the_client_treats_as_no_results()
    {
        var json = await InvokeTool("claims", "zzzz", new NothingSearch());

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("results").GetArrayLength());
        Assert.Equal(KnowledgeSearchTool.NoResultsNote, doc.RootElement.GetProperty("note").GetString());
        var answer = (await new ScriptedChatClient().GetResponseAsync(
            ScriptedChatClientTests.WithToolResult("q", json), ScriptedChatClientTests.Specialist())).Text;
        Assert.Equal(ScriptedChatClient.NoResultsAnswer, answer);
    }

    [Fact]
    public async Task The_real_tool_output_with_hostile_content_yields_only_tool_emitted_refs()
    {
        var json = await InvokeTool("claims", "appeal a rejected claim");

        var answer = (await new ScriptedChatClient().GetResponseAsync(
            ScriptedChatClientTests.WithToolResult("q", json), ScriptedChatClientTests.Specialist())).Text;

        using var doc = JsonDocument.Parse(json);
        var emitted = doc.RootElement.GetProperty("results").EnumerateArray().Select(i => i.GetProperty("ref").GetString()!).ToHashSet();
        var markers = System.Text.RegularExpressions.Regex.Matches(answer, @"\[(S\d+)\]").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(markers);
        Assert.All(markers, m => Assert.Contains(m, emitted));
        Assert.DoesNotContain("S9", answer);
        Assert.DoesNotContain("</source>", answer);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("claims")]
    [InlineData("healthAssessments")]
    public void Last_category_sentence_of_the_real_prompt_is_parsed(string category)
    {
        var prompt = new EmbeddedPromptProvider().Get(OrchestratorPrompt);

        Assert.Contains("The category routed on the previous answered turn was: " + Placeholder + ".", prompt);
        var filled = prompt.Replace(Placeholder, category, StringComparison.Ordinal);

        Assert.Equal(category, ScriptedChatClient.LastCategory(filled, []));
    }

    [Fact]
    public void Last_category_none_in_the_real_prompt_is_ignored()
    {
        var prompt = new EmbeddedPromptProvider().Get(OrchestratorPrompt).Replace(Placeholder, "none", StringComparison.Ordinal);

        Assert.Null(ScriptedChatClient.LastCategory(prompt, []));
    }

    private static async Task<string> InvokeTool(string domain, string query, SupportChat.Core.Knowledge.IKnowledgeSearch? search = null)
    {
        var tool = KnowledgeSearchTool.Create(
            search ?? new KnowledgeFixture(),
            domain, new CitationCollector(), 3, NullLogger.Instance);
        var result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = query });
        return result is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : result?.ToString() ?? string.Empty;
    }

    private sealed class NothingSearch : SupportChat.Core.Knowledge.IKnowledgeSearch
    {
        public Task<IReadOnlyList<SupportChat.Core.Knowledge.KnowledgeChunk>> SearchAsync(
            string domain, string query, int top, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SupportChat.Core.Knowledge.KnowledgeChunk>>([]);
    }
}
