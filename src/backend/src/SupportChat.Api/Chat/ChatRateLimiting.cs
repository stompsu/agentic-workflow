using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SupportChat.Api.Auth;
using SupportChat.Api.Http;
using SupportChat.Core.Options;

namespace SupportChat.Api.Chat;

/// <summary>Per-user (oid) limiter on POST messages, per replica (api-contract 5, 6; plan.md section 5); guests use Guest:TurnsPerMinute.</summary>
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
                var guest = context.RequestServices.GetRequiredService<IOptions<GuestOptions>>().Value;
                var known = CurrentUser.TryGetUser(context.User, out var userId, out var kind);
                var key = known ? userId : "unauthenticated";
                // Guests get their own (stricter) limit; the partition keys stay disjoint because the id formats are disjoint.
                var permits = known && kind == UserKind.Guest ? guest.TurnsPerMinute : limits.TurnsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, permits),
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
