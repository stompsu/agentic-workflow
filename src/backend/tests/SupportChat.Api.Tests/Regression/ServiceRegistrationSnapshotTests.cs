using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportChat.Api.Tests.Regression;

/// <summary>
/// Ordered service registrations of the real Program.cs graph, captured as the first ConfigureTestServices callback
/// (before any test override). Guards the registration order that the ApiHost refactor must keep (ADR 0005 section 1).
/// </summary>
[Collection(StartupFailureCollection.Name)]
public class ServiceRegistrationSnapshotTests
{
    // Framework registrations are included only when they belong to a piece Program.cs adds explicitly.
    private static readonly string[] Markers =
    [
        "SupportChat", "JwtBearer", "Microsoft.Identity", "Authorization", "ResponseCompression", "RateLimiter",
        "RateLimiting", "HttpContextAccessor", "Json.JsonOptions", "KestrelServerOptions",
    ];

    private sealed class CapturingFactory(IReadOnlyDictionary<string, string?> config) : WebApplicationFactory<Program>
    {
        public List<ServiceDescriptor> Captured { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            builder.ConfigureTestServices(services => Captured.AddRange(services));
        }
    }

    [Fact]
    public void Service_registrations_keep_their_order()
    {
        using var factory = new CapturingFactory(ApiFactory.BaseConfig());
        _ = factory.Server; // builds the host
        var lines = new StringBuilder();
        var index = 0;
        foreach (var d in factory.Captured)
        {
            var line = Describe(d);
            if (Markers.Any(m => line.Contains(m, StringComparison.Ordinal)))
            {
                lines.Append(index++.ToString("D3")).Append(' ').AppendLine(line);
            }
        }

        Snapshot.Verify("service-registrations.txt", lines.ToString());
    }

    private static string Describe(ServiceDescriptor d)
    {
        var impl = d.IsKeyedService
            ? (d.KeyedImplementationType?.ToString() ?? (d.KeyedImplementationInstance is { } ki ? "instance:" + ki.GetType() : "factory"))
            : (d.ImplementationType?.ToString() ?? (d.ImplementationInstance is { } i ? "instance:" + i.GetType() : "factory"));
        return $"{d.ServiceType} -> {impl} [{d.Lifetime}]{(d.IsKeyedService ? " keyed" : string.Empty)}";
    }
}
