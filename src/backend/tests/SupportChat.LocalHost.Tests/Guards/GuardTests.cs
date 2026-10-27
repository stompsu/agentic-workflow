using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using SupportChat.Api.Http;
using SupportChat.LocalHost.Guards;

namespace SupportChat.LocalHost.Tests.Guards;

public class GuardTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Last().Value)).Build();

    private static IConfiguration Enabled(params (string Key, string? Value)[] extra) =>
        Config([("LocalMode:Enabled", "true"), .. HostedBlank(), .. extra]);

    // Blank values count as unset and keep the tests independent of the machine they run on.
    private static IEnumerable<(string, string?)> HostedBlank() => LocalModeGuards.HostedEnvironmentVariables.Select(n => (n, (string?)string.Empty));

    [Theory]
    [InlineData("Development", true)]
    [InlineData("development", true)]
    [InlineData("Dev", false)]
    [InlineData("Test", false)]
    [InlineData("Production", false)]
    [InlineData("production", false)]
    [InlineData("Staging", false)]
    public void G1_only_Development_passes(string environment, bool allowed)
    {
        var failures = LocalModeGuards.CheckEnvironment(Enabled(), environment);
        Assert.Equal(allowed, failures.Count == 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("nonsense")]
    public void G2_requires_LocalMode_Enabled_true(string? value)
    {
        var config = Config([("LocalMode:Enabled", value), .. HostedBlank()]);
        Assert.Contains(LocalModeGuards.CheckEnvironment(config, "Development"), m => m.Contains("LocalMode:Enabled"));
    }

    [Theory]
    [InlineData("CONTAINER_APP_NAME")]
    [InlineData("CONTAINER_APP_REVISION")]
    [InlineData("CONTAINER_APP_JOB_NAME")]
    [InlineData("CONTAINER_APP_ENV_DNS_SUFFIX")]
    [InlineData("WEBSITE_SITE_NAME")]
    [InlineData("KUBERNETES_SERVICE_HOST")]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER")]
    public void G3_each_hosted_environment_variable_fails(string name)
    {
        var failures = LocalModeGuards.CheckEnvironment(Enabled((name, "x")), "Development");
        Assert.Contains(failures, m => m.Contains(name));
        Assert.Single(failures);
    }

    [Fact]
    public void G3_list_matches_the_adr()
    {
        Assert.Equal(7, LocalModeGuards.HostedEnvironmentVariables.Length);
    }

    public static TheoryData<string, string, string> BadUrls() => new()
    {
        { "urls", "http://*:5080", "urls" },
        { "urls", "http://+:5080", "urls" },
        { "urls", "http://0.0.0.0:5080", "urls" },
        { "urls", "http://[::]:5080", "urls" },
        { "urls", "http://192.168.1.20:5080", "urls" },
        { "urls", "http://myhost:5080", "urls" },
        { "urls", "http://localhost:5080;http://0.0.0.0:5081", "urls" },
        { "ASPNETCORE_URLS", "http://0.0.0.0:5080", "ASPNETCORE_URLS" },
        { "ASPNETCORE_HTTP_PORTS", "5080", "ASPNETCORE_HTTP_PORTS" },
        { "ASPNETCORE_HTTPS_PORTS", "5443", "ASPNETCORE_HTTPS_PORTS" },
        { "HTTP_PORTS", "5080", "http_ports" },
        { "Kestrel:Endpoints:Any:Url", "http://0.0.0.0:5080", "Kestrel:Endpoints:Any:Url" },
    };

    [Theory]
    [MemberData(nameof(BadUrls))]
    public void G4a_non_loopback_url_settings_fail(string key, string value, string expectedKey)
    {
        var failures = LocalModeGuards.CheckUrls(Enabled((key, value)));
        Assert.Contains(failures, m => m.Contains(expectedKey, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(failures, m => m.Contains(value, StringComparison.Ordinal)); // key names only
    }

    [Theory]
    [InlineData("http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://[::1]:5080")]
    [InlineData("http://localhost:5080;http://127.0.0.1:5081")]
    public void G4a_loopback_urls_pass(string urls)
    {
        Assert.Empty(LocalModeGuards.CheckUrls(Enabled(("urls", urls))));
        Assert.Empty(LocalModeGuards.CheckUrls(Enabled(("Kestrel:Endpoints:Local:Url", urls.Split(';')[0]))));
    }

    [Fact]
    public void G4a_nothing_configured_uses_the_kestrel_default()
    {
        Assert.Empty(LocalModeGuards.CheckUrls(Enabled()));
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Entra")]
    public void G4a_applies_to_every_auth_mode(string auth)
    {
        Assert.NotEmpty(LocalModeGuards.CheckUrls(Enabled(("LocalMode:Auth", auth), ("urls", "http://0.0.0.0:5080"))));
        Assert.NotEmpty(LocalModeValidator.Validate(Enabled(("LocalMode:Auth", auth), ("urls", "http://0.0.0.0:5080"), ("AzureAd:Instance", "https://t.ciamlogin.com/"), ("AzureAd:TenantId", "t"), ("AzureAd:ClientId", "c")), "Development"));
    }

    [Theory]
    [InlineData("--environment", "Production")]
    [InlineData("--environment", "Dev")]
    [InlineData("--environment", "Test")]
    [InlineData("--urls", "http://0.0.0.0:5080")]
    [InlineData("--urls", "http://[::]:5080")]
    public async Task Entry_point_exits_1_for_a_failed_guard_before_building_the_host(string option, string value)
    {
        var args = option == "--urls" ? new[] { "--environment", "Development", option, value } : [option, value];
        using var _ = new ConsoleCapture();
        Assert.Equal(1, await LocalProgram.Main(args));
    }

    [Fact]
    public async Task G7_smoke_check_is_refused()
    {
        using var capture = new ConsoleCapture();
        Assert.Equal(1, await LocalProgram.Main(["--smoke-check"]));
    }

    [Theory]
    [InlineData("http://0.0.0.0:0", true)]
    [InlineData("http://127.0.0.1:0", false)]
    public async Task G4b_non_loopback_binding_stops_the_host(string url, bool violated)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var state = LocalModeGuards.AttachBindingGuard(app);
        var stopped = new TaskCompletionSource();
        app.Lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());

        await app.StartAsync();
        if (violated)
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        else
        {
            await Task.Delay(200);
            Assert.False(stopped.Task.IsCompleted);
        }

        Assert.Equal(violated, state.Violated);
        await app.StopAsync();
    }

    [Fact]
    public void CheckAddresses_treats_unparseable_addresses_as_violations()
    {
        Assert.NotEmpty(LocalModeGuards.CheckAddresses(["http://localhost:5080", "garbage"]));
        Assert.Empty(LocalModeGuards.CheckAddresses(["http://localhost:5080", "http://127.0.0.1:5080", "http://[::1]:5080"]));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("localhost;*")]
    [InlineData("example.com")]
    [InlineData("localhost;192.168.1.1")]
    [InlineData("*.localhost")]
    [InlineData("")]
    public void AllowedHosts_widened_fails_validation(string value)
    {
        var failures = LocalModeValidator.Validate(Enabled(("AllowedHosts", value), ("LocalMode:Knowledge:SourcePath", KnowledgeDir)), "Development");
        Assert.Contains(failures, m => m.Contains("AllowedHosts"));
    }

    [Theory]
    [InlineData("localhost;127.0.0.1;[::1]")]
    [InlineData("localhost:5080;[::1]:5080")]
    public void AllowedHosts_loopback_passes_validation(string value)
    {
        var failures = LocalModeValidator.Validate(Enabled(("AllowedHosts", value), ("LocalMode:Knowledge:SourcePath", KnowledgeDir)), "Development");
        Assert.Empty(failures);
    }

    [Fact]
    public void Validator_rules_name_keys_only()
    {
        var failures = LocalModeValidator.Validate(
            Enabled(
                ("AllowedHosts", "localhost"),
                ("LocalMode:Auth", "Bogus"),
                ("LocalMode:Models", "Bogus"),
                ("LocalMode:LocalAuth:TokenLifetime", "00:01:00"),
                ("LocalMode:Scripted:TokenDelay", "00:00:05"),
                ("LocalMode:Knowledge:SourcePath", "/does/not/exist-xyz")),
            "Development");

        Assert.Contains(failures, m => m.Contains("LocalMode:Auth"));
        Assert.Contains(failures, m => m.Contains("LocalMode:Models"));
        Assert.Contains(failures, m => m.Contains("TokenLifetime"));
        Assert.Contains(failures, m => m.Contains("TokenDelay"));
        Assert.Contains(failures, m => m.Contains("SourcePath"));
        Assert.DoesNotContain(failures, m => m.Contains("Bogus") || m.Contains("exist-xyz"));
    }

    [Theory]
    [InlineData("LocalMode:Auth", "5")]
    [InlineData("LocalMode:Auth", "1")]
    [InlineData("LocalMode:Auth", "-1")]
    [InlineData("LocalMode:Models", "5")]
    [InlineData("LocalMode:Models", "1")]
    [InlineData("LocalMode:Models", "-1")]
    public void Numeric_enum_values_fail_validation(string key, string value)
    {
        var failures = LocalModeValidator.Validate(Enabled(("AllowedHosts", "localhost"), (key, value), ("LocalMode:Knowledge:SourcePath", KnowledgeDir)), "Development");
        Assert.Contains(failures, m => m.Contains(key));
    }

    [Fact]
    public void Entra_and_Foundry_require_their_settings()
    {
        var failures = LocalModeValidator.Validate(
            Enabled(("AllowedHosts", "localhost"), ("LocalMode:Auth", "Entra"), ("LocalMode:Models", "Foundry"), ("AzureAd:TenantId", "<tenant-guid>"), ("Foundry:ProjectEndpoint", "http://x"), ("LocalMode:Knowledge:SourcePath", KnowledgeDir)),
            "Development");

        Assert.Contains(failures, m => m.Contains("AzureAd:Instance"));
        Assert.Contains(failures, m => m.Contains("AzureAd:TenantId"));
        Assert.Contains(failures, m => m.Contains("AzureAd:ClientId"));
        Assert.Contains(failures, m => m.Contains("Foundry:ProjectEndpoint"));

        var ok = LocalModeValidator.Validate(
            Enabled(("AllowedHosts", "localhost"), ("LocalMode:Auth", "Entra"), ("LocalMode:Models", "Foundry"), ("AzureAd:Instance", "https://t.ciamlogin.com/"), ("AzureAd:TenantId", "11111111-2222-3333-4444-555555555555"), ("AzureAd:ClientId", "22222222-2222-3333-4444-555555555555"), ("Foundry:ProjectEndpoint", "https://x.services.ai.azure.com/api/projects/p"), ("LocalMode:Knowledge:SourcePath", KnowledgeDir)),
            "Development");
        Assert.Empty(ok);
    }

    internal static string KnowledgeDir => Path.Combine(AppContext.BaseDirectory, "knowledge");

    // The entry point writes the failure to stderr; keep the test output clean.
    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _error = Console.Error;

        public ConsoleCapture() => Console.SetError(new StringWriter());

        public void Dispose() => Console.SetError(_error);
    }
}

/// <summary>The same rules at host start (ValidateOnStart), which is where WebApplicationFactory-injected configuration is seen.</summary>
public class HostStartGuardTests
{
    private static async Task<IReadOnlyList<string>> StartFailuresAsync(LocalHostFactory factory)
    {
        using (factory)
        {
            var ex = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));
            Assert.NotNull(ex);
            return StartupFailure.Messages(ex);
        }
    }

    [Fact]
    public async Task Valid_configuration_starts_and_is_ready()
    {
        using var factory = new LocalHostFactory();
        using var client = factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/readyz")).StatusCode);
    }

    [Theory]
    [InlineData("Dev")]
    [InlineData("Test")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task G1_other_environments_do_not_start(string environment)
    {
        // WebApplicationFactory applies the environment before the entry point's pre-Build guard (G1) runs, so the entry point
        // returns 1 and the factory reports that Main exited without building a host.
        var failures = await StartFailuresAsync(new LocalHostFactory(environment));
        Assert.NotEmpty(failures);
    }

    [Fact]
    public async Task G2_disabled_does_not_start()
    {
        var failures = await StartFailuresAsync(new LocalHostFactory(c => c["LocalMode:Enabled"] = "false"));
        Assert.Contains(failures, m => m.Contains("LocalMode:Enabled"));
    }

    [Theory]
    [InlineData("CONTAINER_APP_NAME")]
    [InlineData("CONTAINER_APP_REVISION")]
    [InlineData("CONTAINER_APP_JOB_NAME")]
    [InlineData("CONTAINER_APP_ENV_DNS_SUFFIX")]
    [InlineData("WEBSITE_SITE_NAME")]
    [InlineData("KUBERNETES_SERVICE_HOST")]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER")]
    public async Task G3_hosted_environment_does_not_start(string name)
    {
        var failures = await StartFailuresAsync(new LocalHostFactory(c => c[name] = "1"));
        Assert.Contains(failures, m => m.Contains(name));
    }

    [Fact]
    public async Task AllowedHosts_widened_does_not_start()
    {
        var failures = await StartFailuresAsync(new LocalHostFactory(c => c["AllowedHosts"] = "*"));
        Assert.Contains(failures, m => m.Contains("AllowedHosts"));
    }

    [Fact]
    public async Task Missing_knowledge_folder_does_not_start()
    {
        var failures = await StartFailuresAsync(new LocalHostFactory(c => c["LocalMode:Knowledge:SourcePath"] = "no-such-folder"));
        Assert.Contains(failures, m => m.Contains("SourcePath"));
    }

    [Fact]
    public async Task Empty_knowledge_folder_does_not_start()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var failures = await StartFailuresAsync(new LocalHostFactory(c => c["LocalMode:Knowledge:SourcePath"] = dir));
            Assert.Contains(failures, m => m.Contains("no chunks"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
