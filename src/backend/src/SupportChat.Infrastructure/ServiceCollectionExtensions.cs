using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportChat.Core.Health;
using SupportChat.Core.Knowledge;
using SupportChat.Core.Models;
using SupportChat.Core.Options;
using SupportChat.Core.Storage;
using SupportChat.Infrastructure.Azure;
using SupportChat.Infrastructure.Health;
using SupportChat.Infrastructure.Models;
using SupportChat.Infrastructure.Search;
using SupportChat.Infrastructure.Smoke;
using SupportChat.Infrastructure.Storage;
using SupportChat.Infrastructure.Storage.Cosmos;

namespace SupportChat.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Options, startup validation (fails the host start), credential provider, client factory, Cosmos stores,
    /// readiness check and the chat client factory. No Azure client is constructed until first use.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<AzureOptions>().Bind(configuration.GetSection(AzureOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AzureOptions>, AzureStartupOptionsValidator>();
        services.AddSingleton<IValidateOptions<AzureOptions>, GuestStartupOptionsValidator>(); // ADR 0003 rules 12-17
        services.AddOptions<GuestOptions>().Bind(configuration.GetSection(GuestOptions.SectionName));
        services.AddOptions<CosmosOptions>().Bind(configuration.GetSection(CosmosOptions.SectionName));
        services.AddOptions<SearchServiceOptions>().Bind(configuration.GetSection(SearchServiceOptions.SectionName));
        services.AddOptions<FoundryOptions>().Bind(configuration.GetSection(FoundryOptions.SectionName));
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName));
        services.AddOptions<ChatLimitsOptions>().Bind(configuration.GetSection(ChatLimitsOptions.SectionName));

        services.AddSingleton<IAzureCredentialProvider, AzureCredentialProvider>();
        services.AddSingleton<IAzureClientFactory, AzureClientFactory>();
        services.AddSingleton<CosmosClient>(sp => sp.GetRequiredService<IAzureClientFactory>().CreateCosmosClient());
        services.AddSingleton<SearchClient>(sp =>
            sp.GetRequiredService<IAzureClientFactory>().CreateSearchClient(sp.GetRequiredService<IOptions<SearchServiceOptions>>().Value.IndexName));

        services.AddSingleton<CosmosChatContainer>();
        services.AddSingleton(sp => new RetentionPolicy(sp.GetRequiredService<IOptions<ChatLimitsOptions>>(), sp.GetRequiredService<IOptions<GuestOptions>>())); // ttl by user (data-model 1.3)
        services.AddSingleton<IGuestStore, CosmosGuestStore>();
        services.AddSingleton<ISessionStore, CosmosSessionStore>();
        services.AddSingleton<IMessageStore, CosmosMessageStore>();
        services.AddSingleton<IReadinessCheck, CosmosReadinessCheck>();

        services.AddSingleton<IKnowledgeSearch>(sp => new AzureSearchKnowledgeSearch(
            new Lazy<SearchClient>(() => sp.GetRequiredService<SearchClient>()),
            sp.GetRequiredService<IOptions<SearchServiceOptions>>(),
            sp.GetRequiredService<ILogger<AzureSearchKnowledgeSearch>>()));
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSmokeChecks();
        return services;
    }
}
