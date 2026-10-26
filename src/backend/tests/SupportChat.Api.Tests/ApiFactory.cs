using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SupportChat.Core.Health;
using SupportChat.Core.Knowledge;
using SupportChat.Core.Models;
using SupportChat.Core.Storage;
using SupportChat.Infrastructure.Storage.InMemory;

namespace SupportChat.Api.Tests;

/// <summary>No model is ever called from Api.Tests.</summary>
public sealed class FakeChatClientFactory : IChatClientFactory
{
    public Microsoft.Extensions.AI.IChatClient Create(string deploymentName) =>
        throw new InvalidOperationException("Api.Tests must not call a model.");
}

public sealed class FakeKnowledgeSearch : IKnowledgeSearch
{
    public Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(string domain, string query, int top, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<KnowledgeChunk>>([]);
}

public sealed class FakeReadiness : IReadinessCheck
{
    public bool Ready { get; set; } = true;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(Ready);
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages)
            {
                owner._messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
            }
        }
    }
}

/// <summary>
/// The real app (real JwtBearer + ChatUser policy + endpoints) with only the outer edges replaced: signing keys,
/// stores (in-memory, enforcing userId), readiness, time. Nothing here reaches Azure.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public ApiFactory(string environment = "Test", Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null)
    {
        Environment = environment;
        Config = BaseConfig();
        configure?.Invoke(Config);
        ExtraServices = services;
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        Store = new InMemoryChatStore(
            Time,
            new SupportChat.Core.Options.ChatLimitsOptions
            {
                RetentionDays = int.TryParse(Config.GetValueOrDefault("Chat:RetentionDays"), out var days) ? days : null,
            });
    }

    public string Environment { get; }

    public Dictionary<string, string?> Config { get; }

    public Action<IServiceCollection>? ExtraServices { get; }

    public FakeTimeProvider Time { get; }

    public InMemoryChatStore Store { get; }

    public FakeReadiness Readiness { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public static Dictionary<string, string?> BaseConfig() => new()
    {
        ["Azure:AuthMode"] = "Key",
        ["Chat:RetentionDays"] = "30",
        ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-0000000000aa",
        ["Cosmos:Endpoint"] = "https://localhost:8081/",
        ["Cosmos:Key"] = "dGVzdC1rZXk=",
        ["Search:Endpoint"] = "https://example.search.windows.net",
        ["Search:Key"] = "query-key",
        ["AzureAd:Instance"] = "https://tenant.ciamlogin.com/",
        ["AzureAd:TenantId"] = TestTokens.TenantId,
        ["AzureAd:ClientId"] = TestTokens.Audience,
        ["Auth:RequiredScopes:0"] = "access_as_user",
        ["Foundry:ProjectEndpoint"] = "https://example.invalid/api/projects/p",
        ["Workflows:SupportTicket:Agents:Orchestrator:Deployment"] = "test-orchestrator",
        ["Workflows:SupportTicket:Agents:Policy:Deployment"] = "test-specialist",
        ["Workflows:SupportTicket:Agents:Claims:Deployment"] = "test-specialist",
        ["Workflows:SupportTicket:Agents:HealthAssessments:Deployment"] = "test-specialist",
    };

    public HttpClient CreateAuthorizedClient(string oid, string scopes = "access_as_user")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(oid, scopes));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Config));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<ISessionStore>();
            services.RemoveAll<IMessageStore>();
            services.AddSingleton<ISessionStore>(Store);
            services.AddSingleton<IMessageStore>(Store);
            services.RemoveAll<IReadinessCheck>();
            services.AddSingleton<IReadinessCheck>(Readiness);
            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory, FakeChatClientFactory>();
            services.RemoveAll<IKnowledgeSearch>();
            services.AddSingleton<IKnowledgeSearch, FakeKnowledgeSearch>();
            services.AddLogging(logging => logging.AddProvider(Logs));

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                configuration.SigningKeys.Add(TestTokens.Key);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = TestTokens.ValidationParameters();
            });

            ExtraServices?.Invoke(services);
        });
    }
}
