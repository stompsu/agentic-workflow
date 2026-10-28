using System.Text.Json;

namespace AgenticWorkflow.Api.Http;

/// <summary>Reads a bounded JSON body; failures are ProblemDetails results (415 / 413 / 400).</summary>
public static class RequestBody
{
    public const int MaxBytes = 32 * 1024;

    /// <param name="whenEmpty">When set, an empty body yields this value instead of a 400 (optional bodies).</param>
    public static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext context, T? whenEmpty = null)
        where T : class
    {
        if (!context.Request.HasJsonContentType())
        {
            return (null, Problems.Create(StatusCodes.Status415UnsupportedMediaType, Problems.Codes.UnsupportedMediaType));
        }

        if (context.Request.ContentLength > MaxBytes)
        {
            return (null, Problems.Create(StatusCodes.Status413PayloadTooLarge, Problems.Codes.PayloadTooLarge));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                return (null, Problems.Create(StatusCodes.Status413PayloadTooLarge, Problems.Codes.PayloadTooLarge));
            }
        }

        try
        {
            if (buffer.Length == 0 && whenEmpty is not null)
            {
                return (whenEmpty, null);
            }

            var value = buffer.Length == 0 ? null : JsonSerializer.Deserialize<T>(buffer.ToArray(), ApiJson.Options);
            return value is null ? (null, Problems.Validation("body", "A JSON object body is required.")) : (value, null);
        }
        catch (JsonException)
        {
            return (null, Problems.Validation("body", "The request body is not valid JSON."));
        }
    }
}
