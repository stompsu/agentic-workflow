using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SupportChat.Core.Health;
using SupportChat.Core.Knowledge;
using SupportChat.Core.Models;
using SupportChat.Core.Options;
using SupportChat.Core.Storage;
using SupportChat.Infrastructure.Azure;
using SupportChat.Infrastructure.Models;
using SupportChat.Infrastructure.Storage.InMemory;
using SupportChat.LocalHost.Knowledge;
using SupportChat.LocalHost.Models;

namespace SupportChat.LocalHost.Platform;

public static class LocalPlatform
{
    /// <summary>
    /// The "addPlatform" callback of <c>ApiHost.ConfigureServices</c>: in-memory stores and knowledge search, readiness,
    /// options, and the scripted (default) or real Foundry chat client factory. Does NOT call AddInfrastructure.
    /// </summary>
    public static IServiceCollection AddLocalPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IValidateOptions<LocalModeOptions>, LocalModeValidator>();
        // Settings are read when first resolved (not at registration), so the final configuration of the running host applies.
        services.AddOptions<LocalModeOptions>().Configure<IConfiguration>((o, c) => CopyFrom(o, LocalModeOptions.From(c))).ValidateOnStart();
        services.AddOptions<SearchServiceOptions>().Bind(configuration.GetSection(SearchServiceOptions.SectionName));
        services.AddOptions<FoundryOptions>().Bind(configuration.GetSection(FoundryOptions.SectionName));
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName));
        services.AddOptions<ChatLimitsOptions>().Bind(configuration.GetSection(ChatLimitsOptions.SectionName));

        services.AddSingleton(sp => new InMemoryChatStore(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<ChatLimitsOptions>>().Value));
        services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<InMemoryChatStore>());
        services.AddSingleton<IMessageStore>(sp => sp.GetRequiredService<InMemoryChatStore>());

        services.AddSingleton(sp => new LocalKnowledgeCorpus(sp.GetRequiredService<IOptions<LocalModeOptions>>().Value.ResolveKnowledgePath(), sp.GetRequiredService<ILogger<LocalKnowledgeCorpus>>()));
        services.AddSingleton<IKnowledgeSearch>(sp => new InMemoryKnowledgeSearch(
            () => sp.GetRequiredService<LocalKnowledgeCorpus>().Chunks,
            sp.GetRequiredService<IOptions<SearchServiceOptions>>().Value.TopK));
        services.AddSingleton<IReadinessCheck, LocalReadinessCheck>();
        services.AddHostedService<LocalKnowledgeLoader>();

        services.AddSingleton<IChatClientFactory>(sp =>
        {
            var local = sp.GetRequiredService<IOptions<LocalModeOptions>>().Value;
            if (local.Models == LocalModelsMode.Foundry)
            {
                // TokenCredential only (DefaultAzureCredential, Development branch = az login); no model key.
                var credentials = new AzureCredentialProvider(sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<IConfiguration>());
                return new ChatClientFactory(sp.GetRequiredService<IOptions<FoundryOptions>>(), credentials);
            }

            return new ScriptedChatClientFactory(local.TokenDelay);
        });

        return services;
    }

    private static void CopyFrom(LocalModeOptions target, LocalModeOptions source)
    {
        target.Enabled = source.Enabled;
        target.Auth = source.Auth;
        target.Models = source.Models;
        target.TokenLifetime = source.TokenLifetime;
        target.TokenDelay = source.TokenDelay;
        target.KnowledgeSourcePath = source.KnowledgeSourcePath;
    }
}
