using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SupportChat.Api.Auth;
using SupportChat.Api.Http;
using SupportChat.Core.Options;

namespace SupportChat.Api.Chat;

/// <summary>Per-user (oid) limiter on POST messages, per replica (api-contract 5, 6; plan.md section 5).</summary>
public static class ChatRateLimiting
{
    public const string PolicyName = "chat-messages";

    public static IServiceCollection AddChatRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PolicyName, context =>
            {
                var limits = context.RequestServices.GetRequiredService<IOptions<ChatLimitsOptions>>().Value;
                var key = CurrentUser.TryGetUserId(context.User, out var userId) ? userId : "unauthenticated";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, limits.TurnsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
            options.OnRejected = async (context, _) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await Problems.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, Problems.Codes.RateLimited);
            };
        });
        return services;
    }
}
