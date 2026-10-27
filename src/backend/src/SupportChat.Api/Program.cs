using SupportChat.Api.Auth;
using SupportChat.Api.Hosting;
using SupportChat.Infrastructure;
using SupportChat.Infrastructure.Smoke;

var builder = WebApplication.CreateBuilder(args);
ApiHost.ConfigureServices(builder, (services, config) => services.AddInfrastructure(config), (services, config) => services.AddEntraJwtBearer(config));

var app = builder.Build();

// ADR 0003 section 1a: run the managed-identity checks and exit; never serve HTTP.
if (args.Contains(SmokeCheckCommand.Flag))
{
    return await SmokeCheckCommand.RunAsync(app.Services);
}

ApiHost.MapApi(app);
return await ApiHost.RunAsync(app, typeof(Program).Assembly);

public partial class Program;
