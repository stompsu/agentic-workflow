using System.Text;
using SupportChat.Api.Http;

namespace SupportChat.Api.Tests.Regression;

/// <summary>
/// The exact, ordered StartupFailure.Messages list for a fixed set of invalid configurations (ADR 0005 section 1).
/// The messages name configuration keys only, never values.
/// </summary>
[Collection(StartupFailureCollection.Name)]
public class StartupFailureSnapshotTests
{
    private static ApiFactory ProductionManagedIdentity(Action<Dictionary<string, string?>>? more = null) => new("Production", c =>
    {
        c["Azure:AuthMode"] = "ManagedIdentity";
        c["Cosmos:Endpoint"] = "https://account.documents.azure.com:443/";
        c.Remove("Cosmos:Key");
        c.Remove("Search:Key");
        more?.Invoke(c);
    });

    private static readonly (string Name, Func<ApiFactory> Create)[] Cases =
    [
        ("production-key-mode", () => new ApiFactory("Production")),
        ("production-configured-key", () => ProductionManagedIdentity(c => c["Search:Key"] = "query-key")),
        ("production-echo-enabled", () => ProductionManagedIdentity(c => c["Workflows:Echo:Enabled"] = "true")),
        ("missing-retention-days", () => new ApiFactory(configure: c => c.Remove("Chat:RetentionDays"))),
        ("invalid-delete-budget", () => new ApiFactory(configure: c => c["Chat:DeleteUserDataBudget"] = "00:00:01")),
        ("invalid-chat-limits", () => new ApiFactory(configure: c =>
        {
            c["Chat:TurnsPerMinute"] = "0";
            c["Chat:MaxMessageChars"] = "0";
        })),
        ("missing-auth-mode", () => new ApiFactory(configure: c => c["Azure:AuthMode"] = "")),
        ("missing-foundry-endpoint", () => new ApiFactory(configure: c => c.Remove("Foundry:ProjectEndpoint"))),
        ("missing-deployment", () => new ApiFactory(configure: c => c["Workflows:SupportTicket:Agents:Claims:Deployment"] = "")),
        ("placeholder-deployments", () => new ApiFactory(configure: c =>
        {
            foreach (var key in new[] { "Orchestrator", "Policy", "Claims", "HealthAssessments" })
            {
                c.Remove($"Workflows:SupportTicket:Agents:{key}:Deployment");
            }
        })),
    ];

    public static TheoryData<string> CaseNames() => new(Cases.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Failure_messages_are_unchanged(string name)
    {
        using var factory = Cases.Single(c => c.Name == name).Create();
        var ex = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));
        Assert.NotNull(ex);

        var text = new StringBuilder();
        foreach (var message in StartupFailure.Messages(ex))
        {
            text.AppendLine(message);
        }

        Snapshot.Verify($"startup-failure-{name}.txt", text.ToString());
    }
}
