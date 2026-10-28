using System.Text.Json;
using AgenticWorkflow.Core.Serialization;
using AgenticWorkflow.Infrastructure.Storage.Cosmos;

namespace AgenticWorkflow.Infrastructure.Tests.Storage;

/// <summary>Cosmos rejects "ttl": null, so the documents must omit the property when it is unset (data-model 1.2).</summary>
public class TtlSerializationTests
{
    private static readonly JsonSerializerOptions Options = JsonDefaults.Create();

    private static SessionDoc Session(int? ttl) =>
        new() { Id = "i", UserId = "u", SessionId = "i", WorkflowId = "wf", Ttl = ttl };

    [Fact]
    public void Session_without_ttl_omits_the_property()
    {
        var json = JsonSerializer.Serialize(Session(null), Options);
        Assert.DoesNotContain("ttl", json);
    }

    [Fact]
    public void Session_with_ttl_writes_the_integer()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Session(2592000), Options));
        Assert.Equal(2592000, doc.RootElement.GetProperty("ttl").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7776000)]
    public void Message_ttl_follows_the_argument(int? ttl)
    {
        var record = Msg.User("s", "hi", DateTimeOffset.UtcNow);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(MessageDoc.From("u", record, ttl), Options));
        if (ttl is null)
        {
            Assert.False(doc.RootElement.TryGetProperty("ttl", out _));
        }
        else
        {
            Assert.Equal(ttl, doc.RootElement.GetProperty("ttl").GetInt32());
        }
    }

    [Fact]
    public void Container_bicep_enables_ttl_with_default_minus_one()
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "infra", "modules", "cosmos.bicep");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }
        }

        if (path is null)
        {
            return; // repository layout not available (for example a copied test binary)
        }

        Assert.Contains("defaultTtl: -1", File.ReadAllText(path));
    }
}
