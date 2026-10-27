using System.Diagnostics;
using System.Text.Json;

namespace SupportChat.Api.Http;

/// <summary>
/// ProblemDetails (RFC 9457) writer with the stable <c>code</c> extension (api-contract 5). Written directly so that
/// it works regardless of the request's Accept header (the SSE endpoint is called with Accept: text/event-stream).
/// </summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    public static class Codes
    {
        public const string ValidationFailed = "validation_failed";
        public const string Unauthorized = "unauthorized";
        public const string Forbidden = "forbidden";
        public const string NotFound = "not_found";
        public const string TurnInProgress = "turn_in_progress";
        public const string WorkflowUnavailable = "workflow_unavailable";
        public const string PayloadTooLarge = "payload_too_large";
        public const string UnsupportedMediaType = "unsupported_media_type";
        public const string RateLimited = "rate_limited";
        public const string InternalError = "internal_error";
        public const string DeletionIncomplete = "deletion_incomplete";
        public const string GuestAccessDisabled = "guest_access_disabled";
        public const string GuestExpired = "guest_expired";
        public const string GuestQuotaExceeded = "guest_quota_exceeded";
        public const string BotCheckFailed = "bot_check_failed";
    }

    public static IResult Create(int status, string code, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new ProblemResult(status, code, errors);

    public static IResult NotFound() => Create(StatusCodes.Status404NotFound, Codes.NotFound);

    /// <summary>503 deletion_incomplete with Retry-After: 1 (api-contract 2.8).</summary>
    public static IResult DeletionIncomplete() => new RetryAfterResult(
        Create(StatusCodes.Status503ServiceUnavailable, Codes.DeletionIncomplete), "1");

    /// <summary>401 guest_expired: the guest identity has ended (token expired or data deleted). The client must not retry.</summary>
    public static IResult GuestExpired() => Create(StatusCodes.Status401Unauthorized, Codes.GuestExpired);

    /// <summary>429 guest_quota_exceeded WITHOUT Retry-After (api-contract 2.9).</summary>
    public static IResult GuestQuotaExceeded() => Create(StatusCodes.Status429TooManyRequests, Codes.GuestQuotaExceeded);

    /// <summary>429 rate_limited with Retry-After in whole seconds (minimum 1).</summary>
    public static IResult RateLimited(TimeSpan retryAfter) =>
        new RetryAfterResult(
            Create(StatusCodes.Status429TooManyRequests, Codes.RateLimited),
            Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static IResult TurnInProgress() => Create(StatusCodes.Status409Conflict, Codes.TurnInProgress);

    public static IResult Validation(string field, string message) =>
        Create(StatusCodes.Status400BadRequest, Codes.ValidationFailed, new Dictionary<string, string[]> { [field] = [message] });

    public static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => Codes.ValidationFailed,
        StatusCodes.Status401Unauthorized => Codes.Unauthorized,
        StatusCodes.Status403Forbidden => Codes.Forbidden,
        StatusCodes.Status404NotFound => Codes.NotFound,
        StatusCodes.Status413PayloadTooLarge => Codes.PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => Codes.UnsupportedMediaType,
        StatusCodes.Status429TooManyRequests => Codes.RateLimited,
        StatusCodes.Status409Conflict => Codes.TurnInProgress,
        _ => Codes.InternalError,
    };

    public static async Task WriteAsync(
        HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var body = new Dictionary<string, object?>
        {
            ["type"] = TypeFor(status),
            ["title"] = TitleFor(status),
            ["status"] = status,
            ["code"] = code,
            ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier,
        };
        if (errors is { Count: > 0 })
        {
            body["errors"] = errors;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        await JsonSerializer.SerializeAsync(context.Response.Body, body, ApiJson.Options, context.RequestAborted);
    }

    private static string TypeFor(int status) => status switch
    {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        503 => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        413 => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        415 => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        429 => "https://tools.ietf.org/html/rfc6585#section-4",
        _ => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
    };

    private static string TitleFor(int status) => status switch
    {
        400 => "Validation failed",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        413 => "Payload too large",
        415 => "Unsupported media type",
        429 => "Too many requests",
        503 => "Deletion incomplete",
        _ => "An unexpected error occurred",
    };

    private sealed class RetryAfterResult(IResult inner, string seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = seconds;
            return inner.ExecuteAsync(httpContext);
        }
    }

    private sealed class ProblemResult(int status, string code, IReadOnlyDictionary<string, string[]>? errors) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext, status, code, errors);
    }
}
