using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Infrastructure.Azure;
using AgenticWorkflow.Infrastructure.Smoke;

namespace AgenticWorkflow.Api.Tests;

public class SmokeCheckTests
{
    private const string SecretMessage = "https://secret-account.documents.azure.com key=SUPER-SECRET";

    private sealed class FakeProbe(string name, Exception? failure = null, bool hang = false) : ISmokeProbe
    {
        public string Name => name;

        public int Runs { get; private set; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Runs++;
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    private static ApiFactory MiFactory(FakeProbe[] probes, Action<Dictionary<string, string?>>? configure = null) =>
        new(
            "Test",
            c =>
            {
                c["Azure:AuthMode"] = "ManagedIdentity";
                c.Remove("Cosmos:Key");
                c.Remove("Search:Key");
                configure?.Invoke(c);
            },
            s =>
            {
                s.RemoveAll<ISmokeProbe>();
                foreach (var probe in probes)
                {
                    s.AddSingleton<ISmokeProbe>(probe);
                }
            });

    private static FakeProbe[] Passing() => [new("Cosmos"), new("Search"), new("Foundry")];

    [Fact]
    public async Task All_checks_passing_exits_0_and_logs_each_pass()
    {
        var probes = Passing();
        using var factory = MiFactory(probes);

        Assert.Equal(0, await SmokeCheckCommand.RunAsync(factory.Services));

        Assert.All(probes, p => Assert.Equal(1, p.Runs));
        foreach (var name in new[] { "Cosmos", "Search", "Foundry" })
        {
            Assert.Contains(factory.Logs.Messages, m => m.Contains($"Smoke check {name}: PASS", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Cosmos")]
    [InlineData("Search")]
    [InlineData("Foundry")]
    public async Task Each_failing_check_exits_1_and_the_others_still_run(string failing)
    {
        var probes = Passing().Select(p => p.Name == failing ? new FakeProbe(p.Name, new UnauthorizedAccessException(SecretMessage)) : p).ToArray();
        using var factory = MiFactory(probes);

        Assert.Equal(1, await SmokeCheckCommand.RunAsync(factory.Services));

        Assert.All(probes, p => Assert.Equal(1, p.Runs));
        Assert.Contains(factory.Logs.Messages, m => m.Contains($"Smoke check {failing}: FAIL (UnauthorizedAccessException)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_hanging_check_times_out_and_fails()
    {
        using var factory = MiFactory([new FakeProbe("Cosmos", hang: true), new FakeProbe("Search")]);

        Assert.Equal(1, await SmokeCheckCommand.RunAsync(factory.Services, TimeSpan.FromMilliseconds(100)));

        Assert.Contains(factory.Logs.Messages, m => m.Contains("Smoke check Cosmos: FAIL (TaskCanceledException)", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Messages, m => m.Contains("Smoke check Search: PASS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exception_messages_are_never_logged()
    {
        using var factory = MiFactory([new FakeProbe("Cosmos", new InvalidOperationException(SecretMessage, new Exception(SecretMessage)))]);

        Assert.Equal(1, await SmokeCheckCommand.RunAsync(factory.Services));

        Assert.DoesNotContain(factory.Logs.Messages, m => m.Contains("SUPER-SECRET", StringComparison.Ordinal) || m.Contains("secret-account", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Messages, m => m.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Key_mode_is_refused_and_no_probe_runs()
    {
        var probes = Passing();
        using var factory = new ApiFactory("Test", services: s =>
        {
            s.RemoveAll<ISmokeProbe>();
            foreach (var probe in probes)
            {
                s.AddSingleton<ISmokeProbe>(probe);
            }
        });

        Assert.Equal(1, await SmokeCheckCommand.RunAsync(factory.Services));

        Assert.All(probes, p => Assert.Equal(0, p.Runs));
        Assert.Contains(factory.Logs.Messages, m => m.Contains("requires Azure:AuthMode=ManagedIdentity", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Cosmos:Key")]
    [InlineData("Search:Key")]
    public async Task Configured_keys_are_refused_even_in_ManagedIdentity_mode(string key)
    {
        var probes = Passing();
        using var factory = MiFactory(probes, c => c[key] = "some-key");

        Assert.Equal(1, await SmokeCheckCommand.RunAsync(factory.Services));

        Assert.All(probes, p => Assert.Equal(0, p.Runs));
        Assert.Contains(factory.Logs.Messages, m => m.Contains($"requires {key} to be unset", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Messages, m => m.Contains("some-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_validation_runs_first_and_failure_exits_1()
    {
        var probes = Passing();
        using var factory = new ApiFactory("Test", c =>
        {
            c["Azure:AuthMode"] = "ManagedIdentity";
            c.Remove("Cosmos:Key");
            c.Remove("Search:Key");
        }, s =>
        {
            s.RemoveAll<ISmokeProbe>();
            foreach (var probe in probes)
            {
                s.AddSingleton<ISmokeProbe>(probe);
            }

            s.RemoveAll<IStartupValidator>();
            s.AddSingleton<FailingStartupValidator>();
            s.AddSingleton<IStartupValidator>(sp => sp.GetRequiredService<FailingStartupValidator>());
        });

        factory.Services.GetRequiredService<FailingStartupValidator>().Fail = true; // the host itself started fine
        Assert.Equal(1, await SmokeCheckCommand.RunAsync(factory.Services));

        Assert.All(probes, p => Assert.Equal(0, p.Runs));
        Assert.Contains(factory.Logs.Messages, m => m.Contains("startup validation failed (OptionsValidationException)", StringComparison.Ordinal));
    }

    private sealed class FailingStartupValidator : IStartupValidator
    {
        public bool Fail { get; set; }

        public void Validate()
        {
            if (Fail)
            {
                Throw();
            }
        }

        private static void Throw() => throw new OptionsValidationException("x", typeof(AzureOptions), ["Cosmos:Endpoint must be valid."]);
    }

    [Fact]
    public async Task Foundry_probe_acquires_a_token_for_the_foundry_scope_and_skips_metadata_without_an_endpoint()
    {
        var credential = new RecordingCredential();
        var metadataCalls = 0;
        var probe = new FoundrySmokeProbe(
            new Provider(credential), Options.Create(new FoundryOptions()), (_, _, _) => { metadataCalls++; return Task.CompletedTask; });

        await probe.RunAsync(CancellationToken.None);

        Assert.Equal(["https://ai.azure.com/.default"], credential.Scopes);
        Assert.Equal(0, metadataCalls);
    }

    [Fact]
    public async Task Foundry_probe_makes_the_metadata_call_when_the_endpoint_is_set()
    {
        var credential = new RecordingCredential();
        Uri? seen = null;
        var probe = new FoundrySmokeProbe(
            new Provider(credential),
            Options.Create(new FoundryOptions { ProjectEndpoint = "https://acct.services.ai.azure.com/api/projects/p" }),
            (uri, _, _) => { seen = uri; return Task.CompletedTask; });

        await probe.RunAsync(CancellationToken.None);

        Assert.Equal(new Uri("https://acct.services.ai.azure.com/api/projects/p"), seen);
    }

    private sealed class Provider(TokenCredential credential) : IAzureCredentialProvider
    {
        public TokenCredential Credential => credential;
    }

    private sealed class RecordingCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new AccessToken("t", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
