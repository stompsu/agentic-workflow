using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace SupportChat.Api.Tests;

/// <summary>
/// Starts the host with the production service graph exactly as Program.cs registers it: no ApiFactory service
/// replacements. Regression for a startup hang (IOptions&lt;AzureOptions&gt; validation -> workflow registry ->
/// SearchClient -> client factory -> IOptions&lt;AzureOptions&gt;) that only appeared with the real graph.
/// </summary>
public class RealHostStartupTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Test")]
    public async Task Appsettings_defaults_fail_fast_with_real_service_graph_instead_of_hanging(string environment)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment(environment));

        var start = Task.Run(() => factory.CreateClient());
        var finished = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, start), "Host start hung instead of failing validation.");
        await Assert.ThrowsAnyAsync<Exception>(() => start);
    }

    [Theory]
    [InlineData("Production", "")]
    [InlineData("Test", "--smoke-check")]
    public async Task Real_process_exits_1_quickly_with_key_only_messages(string environment, string arg)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "SupportChat.Api.dll");
        var info = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(dll);
        if (arg.Length > 0)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        info.Environment["Azure__AuthMode"] = "Key";
        using var process = System.Diagnostics.Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Process did not exit; startup hung.");
        }

        var output = await stdout + await stderr;
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("Cosmos:Endpoint", output);
        Assert.Contains("Orchestrator:Deployment", output);
        Assert.DoesNotContain("<tenant-guid>", output.Replace("AzureAd:TenantId", ""));
        Assert.DoesNotContain("<chat-deployment", output);
    }
}
