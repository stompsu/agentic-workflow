using System.Text.Json;
using SupportChat.Core.Serialization;

namespace SupportChat.Api.Http;

public static class ApiJson
{
    /// <summary>camelCase, camelCase string enums, UTC millisecond timestamps; nulls are written (title: null).</summary>
    public static readonly JsonSerializerOptions Options = JsonDefaults.Create(omitNulls: false);
}
