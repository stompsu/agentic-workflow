using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace SupportChat.Api.Tests.Regression;

public class EndpointSnapshotTests
{
    [Fact]
    public void Route_endpoints_are_unchanged()
    {
        using var factory = new ApiFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var lines = endpoints.Select(e =>
        {
            var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } m
                ? string.Join(",", m.OrderBy(x => x, StringComparer.Ordinal))
                : "*";
            var policies = string.Join(",", e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy ?? "(default)").Distinct().OrderBy(x => x, StringComparer.Ordinal));
            var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var limiter = string.Join(",", e.Metadata.GetOrderedMetadata<EnableRateLimitingAttribute>()
                .Select(a => a.PolicyName ?? "(none)"));
            return $"{e.RoutePattern.RawText} [{methods}] authorize={(policies.Length > 0 ? policies : "-")} " +
                   $"anonymous={anonymous} ratelimit={(limiter.Length > 0 ? limiter : "-")}";
        }).OrderBy(l => l, StringComparer.Ordinal);

        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.AppendLine(line);
        }

        Snapshot.Verify("endpoints.txt", text.ToString());
    }
}
