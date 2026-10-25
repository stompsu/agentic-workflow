using Microsoft.Extensions.DependencyInjection;
using SupportChat.Agents;

namespace SupportChat.Api.Tests;

public static class Fixtures
{
    /// <summary>The standard test app: Test environment, Key mode, the throwaway workflow registered.</summary>
    public static ApiFactory WithThrowawayWorkflow() =>
        new(
            configure: c => c["Foundry:ProjectEndpoint"] = "https://example.invalid/api/projects/p",
            services: s => s.AddWorkflow<ThrowawayWorkflow>());
}
