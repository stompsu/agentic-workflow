using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticWorkflow.Api.Tests;

public static class Helpers
{
    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    /// <summary>Response body as JSON with the (per-request) traceId removed, for "identical except traceId" checks.</summary>
    public static async Task<string> BodyWithoutTraceIdAsync(this HttpResponseMessage response)
    {
        var node = (await response.ReadJsonAsync()).AsObject();
        Assert.True(node.Remove("traceId"), "problem body must contain traceId");
        return node.ToJsonString();
    }

    public static async Task<string> CreateSessionAsync(this HttpClient client, string workflowId = "throwaway-test", string? title = null)
    {
        var body = title is null ? $$"""{"workflowId":"{{workflowId}}"}""" : $$"""{"workflowId":"{{workflowId}}","title":"{{title}}"}""";
        var response = await client.PostAsync("/api/sessions", Json(body));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync())["id"]!.GetValue<string>();
    }

    public static IEnumerable<string> AllPropertyNames(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o)
                {
                    yield return name;
                    foreach (var inner in AllPropertyNames(value))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    foreach (var inner in AllPropertyNames(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    public static readonly string[] InternalFields = ["userId", "ttl", "activeTurn", "state", "workflowMetadata", "schemaVersion", "type", "_etag", "messageCount"];
}
