using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SupportChat.LocalHost.Tests;

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
/// The real LocalHost (real Program composition, real local issuer and validation). The entry point reads its first
/// settings before WebApplicationFactory can inject configuration, so the process environment is pinned to Development
/// once; per-test environment and configuration then apply to the started host (and its ValidateOnStart rules).
/// </summary>
public class LocalHostFactory : WebApplicationFactory<LocalProgram>
{
    static LocalHostFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
    }

    public LocalHostFactory()
        : this("Development", null)
    {
    }

    public LocalHostFactory(Action<Dictionary<string, string?>> configure)
        : this("Development", configure)
    {
    }

    public LocalHostFactory(string environment, Action<Dictionary<string, string?>>? configure = null)
    {
        Environment_ = environment;
        Config = new Dictionary<string, string?>
        {
            ["LocalMode:Enabled"] = "true",
            ["LocalMode:Scripted:TokenDelay"] = "00:00:00",
        };

        // Deterministic even when the tests run inside a container: an empty value counts as unset.
        foreach (var name in SupportChat.LocalHost.Guards.LocalModeGuards.HostedEnvironmentVariables)
        {
            Config[name] = string.Empty;
        }

        configure?.Invoke(Config);
    }

    public string Environment_ { get; }

    public Dictionary<string, string?> Config { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment_);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Config));
        builder.ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(Logs)));
    }

    public async Task<string> GetTokenAsync(string user)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/local-auth/token", new { user });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["accessToken"]!.GetValue<string>();
    }

    public async Task<HttpClient> CreateUserClientAsync(string user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await GetTokenAsync(user));
        return client;
    }
}

public sealed record SseEvent(string Type, JsonNode Data);

public static class SseReader
{
    public static async Task<List<SseEvent>> ReadAllAsync(HttpResponseMessage response)
    {
        var events = new List<SseEvent>();
        var body = await response.Content.ReadAsStringAsync();
        foreach (var block in body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? type = null;
            string? data = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    type = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                }
            }

            if (type is not null && data is not null)
            {
                events.Add(new SseEvent(type, JsonNode.Parse(data)!));
            }
        }

        return events;
    }
}

public static class Http
{
    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static HttpRequestMessage Post(string url, string json, string? contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8);
        if (contentType is null)
        {
            request.Content.Headers.ContentType = null;
        }
        else
        {
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }

        return request;
    }
}

/// <summary>Shared-fixture flavour (xunit needs exactly one public constructor).</summary>
public sealed class DefaultLocalHost : LocalHostFactory
{
    public DefaultLocalHost()
    {
    }
}
