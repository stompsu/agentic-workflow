using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SupportChat.Agents;
using SupportChat.Core.Workflows;

namespace SupportChat.Api.Tests;

public class WorkflowEndpointTests
{
    [Fact]
    public async Task A_workflow_registered_by_one_line_appears_in_GET_workflows_and_is_accepted_by_POST_sessions()
    {
        using var factory = Fixtures.WithThrowawayWorkflow();
        var client = factory.CreateAuthorizedClient(TestTokens.NewOid());

        var list = await client.GetAsync("/api/workflows");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var item = (await list.ReadJsonAsync())["items"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "throwaway-test")!;
        Assert.Equal("throwaway-test", item["id"]!.GetValue<string>());
        Assert.Equal("Throwaway", item["displayName"]!.GetValue<string>());
        Assert.Equal("A workflow registered only by tests.", item["description"]!.GetValue<string>());

        var created = await client.PostAsync("/api/sessions", Helpers.Json("""{"workflowId":"throwaway-test"}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("throwaway-test", (await created.ReadJsonAsync())["workflowId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Default_registry_lists_only_SupportTicket_and_POST_sessions_rejects_any_workflow()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateAuthorizedClient(TestTokens.NewOid());

        var ids = (await (await client.GetAsync("/api/workflows")).ReadJsonAsync())["items"]!.AsArray().Select(n => n!["id"]!.GetValue<string>());
        Assert.Equal(["support-ticket"], ids);
        var response = await client.PostAsync("/api/sessions", Helpers.Json("""{"workflowId":"throwaway-test"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("validation_failed", body["code"]!.GetValue<string>());
        Assert.NotNull(body["errors"]!["workflowId"]);
    }

    [Fact]
    public async Task Order_is_registration_order()
    {
        using var factory = new ApiFactory(
            configure: c => c["Foundry:ProjectEndpoint"] = "https://example.invalid/api/projects/p",
            services: s =>
            {
                s.AddWorkflow<SecondWorkflow>();
                s.AddWorkflow<ThrowawayWorkflow>();
            });
        var client = factory.CreateAuthorizedClient(TestTokens.NewOid());
        var ids = (await (await client.GetAsync("/api/workflows")).ReadJsonAsync())["items"]!.AsArray().Select(n => n!["id"]!.GetValue<string>());
        Assert.Equal(["support-ticket", "second-one", "throwaway-test"], ids);
    }

    private sealed class SecondWorkflow : IWorkflowDefinition
    {
        public string Id => "second-one";

        public string DisplayName => "Second";

        public string Description => "";

        public IAsyncEnumerable<ChatStreamEvent> RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Fake(string id, string name) : IWorkflowDefinition
    {
        public string Id => id;

        public string DisplayName => name;

        public string Description => "d";

        public IAsyncEnumerable<ChatStreamEvent> RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Theory]
    [InlineData("Has-Upper", "x")]
    [InlineData("under_score", "x")]
    [InlineData("", "x")]
    [InlineData("ok-id", " ")]
    public void Registry_rejects_invalid_definitions(string id, string name) =>
        Assert.Throws<InvalidOperationException>(() => new WorkflowRegistry([new Fake(id, name)]));

    [Fact]
    public void Registry_rejects_duplicate_ids() =>
        Assert.Throws<InvalidOperationException>(() => new WorkflowRegistry([new Fake("a", "A"), new Fake("a", "B")]));

    [Fact]
    public void Registry_resolves_by_id_and_keeps_order()
    {
        var registry = new WorkflowRegistry([new Fake("b", "B"), new Fake("a", "A")]);
        Assert.Equal(["b", "a"], registry.List().Select(w => w.Id));
        Assert.True(registry.TryGet("a", out var found));
        Assert.Equal("A", found!.DisplayName);
        Assert.False(registry.TryGet("zzz", out _));
    }
}
