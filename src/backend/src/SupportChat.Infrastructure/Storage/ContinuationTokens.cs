using System.Buffers.Text;
using System.Text;
using SupportChat.Core.Storage;

namespace SupportChat.Infrastructure.Storage;

/// <summary>Opaque base64url wrapper around the underlying store's continuation token (api-contract 2.4).</summary>
internal static class ContinuationTokens
{
    public static string Encode(string inner) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(inner));

    public static string Decode(string token)
    {
        if (token.Length is 0 or > TurnRules.MaxContinuationTokenLength)
        {
            throw new InvalidContinuationTokenException();
        }

        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        }
        catch (FormatException ex)
        {
            throw new InvalidContinuationTokenException(ex);
        }
    }
}
