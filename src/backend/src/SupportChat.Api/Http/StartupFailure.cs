using Microsoft.Extensions.Options;

namespace SupportChat.Api.Http;

/// <summary>Flattens a host start exception into messages. Validation messages name configuration keys only, never values.</summary>
public static class StartupFailure
{
    public static IReadOnlyList<string> Messages(Exception exception)
    {
        var messages = new List<string>();
        Collect(exception, messages);
        return messages.Count > 0 ? messages : [exception.GetType().Name];
    }

    private static void Collect(Exception exception, List<string> messages)
    {
        switch (exception)
        {
            case AggregateException aggregate:
                foreach (var inner in aggregate.InnerExceptions)
                {
                    Collect(inner, messages);
                }

                break;
            case OptionsValidationException validation:
                messages.AddRange(validation.Failures);
                break;
            default:
                messages.Add($"{exception.GetType().Name}: {exception.Message}");
                break;
        }
    }
}
