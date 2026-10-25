using System.ClientModel;
using Azure;
using SupportChat.Core.Workflows;

namespace SupportChat.Api.Chat;

/// <summary>
/// Stream error codes and their user-safe messages (api-contract 4.4). The backend owns the text; workflows only
/// supply a code. Codes outside the table become <see cref="InternalError"/>.
/// </summary>
public static class StreamErrors
{
    public const string ModelError = "model_error";
    public const string UpstreamThrottled = "upstream_throttled";
    public const string ContentFiltered = "content_filtered";
    public const string Timeout = "timeout";
    public const string WorkflowError = "workflow_error";
    public const string InternalError = "internal_error";

    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        [ModelError] = "The assistant could not answer. Please try again.",
        [UpstreamThrottled] = "The service is busy. Please try again in a moment.",
        [ContentFiltered] = "This request could not be processed. Please rephrase.",
        [Timeout] = "The answer took too long. Please try again.",
        [WorkflowError] = "Something went wrong. Please try again.",
        [InternalError] = "Something went wrong. Please try again.",
    };

    public static bool IsKnown(string? code) => code is not null && Messages.ContainsKey(code);

    /// <summary>Returns the code itself when it is in the contract table, otherwise <see cref="InternalError"/>.</summary>
    public static string Normalize(string? code) => IsKnown(code) ? code! : InternalError;

    public static string MessageFor(string code) => Messages[Normalize(code)];

    /// <summary>
    /// Exception mapping (ADR 0002 section 3). Cancellation is NOT handled here (the caller decides between client
    /// abort, timeout and internal error). Only exception types and status codes are inspected, never content.
    /// </summary>
    public static string CodeFor(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case WorkflowException w:
                    return Normalize(w.Code);
                case ClientResultException c:
                    return FromModelError(c.Status, c.Message, null);
                case RequestFailedException r:
                    return FromModelError(r.Status, r.Message, r.ErrorCode);
            }
        }

        return InternalError;
    }

    private static string FromModelError(int status, string message, string? errorCode)
    {
        if (status == 429)
        {
            return UpstreamThrottled;
        }

        if (Contains(errorCode, "content_filter") || Contains(errorCode, "ResponsibleAIPolicyViolation")
            || Contains(message, "content_filter") || Contains(message, "ResponsibleAIPolicyViolation"))
        {
            return ContentFiltered;
        }

        return ModelError;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
}
