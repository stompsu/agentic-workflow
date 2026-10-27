using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SupportChat.LocalHost.Auth;

namespace SupportChat.LocalHost.Tests.Auth;

/// <summary>The production Api host (real Entra scheme, metadata stubbed offline) with the knobs the LocalHost tests need.</summary>
public sealed class ApiHostFactory : WebApplicationFactory<global::Program>
{
    public const string Tenant = "11111111-2222-3333-4444-555555555555";
    public const string ClientId = "99999999-8888-7777-6666-555555555555";
    public static string EntraIssuer => $"https://{Tenant}.ciamlogin.com/{Tenant}/v2.0";

    /// <summary>A valid Entra-shaped access token signed with <see cref="EntraKey"/>.</summary>
    public static string MintEntraToken(string oid = "aaaaaaaa-0000-0000-0000-000000000001")
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = EntraIssuer,
            Audience = ClientId,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = DateTime.UtcNow.AddMinutes(30),
            Claims = new Dictionary<string, object> { ["tid"] = Tenant, ["ver"] = "2.0", ["oid"] = oid, ["scp"] = "access_as_user" },
            SigningCredentials = new SigningCredentials(EntraKey, SecurityAlgorithms.HmacSha256),
        };
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(descriptor);
    }
    public static readonly SymmetricSecurityKey EntraKey = new(System.Text.Encoding.UTF8.GetBytes("entra-test-signing-key-entra-test-signing-key-01"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "*",
            ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-0000000000aa",
            ["Azure:AuthMode"] = "Key",
            ["Chat:RetentionDays"] = "30",
            ["Cosmos:Endpoint"] = "https://localhost:8081/",
            ["Cosmos:Key"] = "dGVzdC1rZXk=",
            ["Search:Endpoint"] = "https://example.search.windows.net",
            ["Search:Key"] = "query-key",
            ["AzureAd:Instance"] = "https://tenant.ciamlogin.com/",
            ["AzureAd:TenantId"] = Tenant,
            ["AzureAd:ClientId"] = ClientId,
            ["Foundry:ProjectEndpoint"] = "https://example.invalid/api/projects/p",
            ["Workflows:SupportTicket:Agents:Orchestrator:Deployment"] = "o",
            ["Workflows:SupportTicket:Agents:Policy:Deployment"] = "s",
            ["Workflows:SupportTicket:Agents:Claims:Deployment"] = "s",
            ["Workflows:SupportTicket:Agents:HealthAssessments:Deployment"] = "s",
        }));
        builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = EntraIssuer };
            configuration.SigningKeys.Add(EntraKey);
            options.Configuration = configuration;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = EntraIssuer,
                ValidAudience = ClientId,
                IssuerSigningKey = EntraKey,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                NameClaimType = "name",
            };
        }));
    }
}

public class ProductionIsolationTests
{
    [Fact]
    public async Task Api_rejects_a_token_signed_by_the_LocalHost_issuer()
    {
        using var local = new LocalHostFactory();
        var token = await local.GetTokenAsync("alice");

        using var api = new ApiHostFactory();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workflows")).StatusCode);

        // Positive control: the same host accepts a valid Entra-shaped token, so the 401 above is about the issuer.
        using var entra = api.CreateClient();
        entra.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiHostFactory.MintEntraToken());
        Assert.Equal(HttpStatusCode.OK, (await entra.GetAsync("/api/workflows")).StatusCode);

        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/workflows")).StatusCode);
    }

    [Fact]
    public async Task Api_has_no_local_token_endpoint()
    {
        using var api = new ApiHostFactory();
        using var client = api.CreateClient();
        var response = await client.PostAsync("/local-auth/token", Http.Json("""{"user":"alice"}"""));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
    }

    [Fact]
    public void MapInboundClaims_is_equal_in_the_Api_and_the_LocalHost()
    {
        using var api = new ApiHostFactory();
        using var local = new LocalHostFactory();

        var production = api.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var localOptions = local.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(production.MapInboundClaims, localOptions.MapInboundClaims);
        Assert.Equal(LocalJwtBearerConfigure.ProductionMapInboundClaims, production.MapInboundClaims);
    }

    [Fact]
    public void Auth_Entra_registers_the_production_scheme_and_no_issuer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMode:Auth"] = "Entra",
            ["AzureAd:Instance"] = "https://tenant.ciamlogin.com/",
            ["AzureAd:TenantId"] = ApiHostFactory.Tenant,
            ["AzureAd:ClientId"] = ApiHostFactory.ClientId,
        }).Build();
        var services = new ServiceCollection().AddLogging();
        LocalProgram.AddJwtBearer(services, config);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(LocalTokenIssuer));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(LocalJwtBearerConfigure));

        var local = new ServiceCollection().AddLogging();
        LocalProgram.AddJwtBearer(local, new ConfigurationBuilder().Build());
        Assert.Contains(local, d => d.ServiceType == typeof(LocalTokenIssuer));
    }
}
