using System.Net;

namespace SupportChat.Api.Tests;

/// <summary>ADR 0005 section 7: the local token endpoint exists only in the LocalHost; the production Api never serves it.</summary>
public class LocalAuthAbsentTests
{
    [Theory]
    [InlineData("/local-auth/token")]
    [InlineData("/api/local-auth/token")]
    public async Task Local_token_endpoint_never_returns_200_on_the_Api_host(string route)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(route, Helpers.Json("""{"user":"alice"}"""));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
    }
}
