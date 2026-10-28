namespace AgenticWorkflow.Core.Security;

/// <summary>The authenticated user. <see cref="UserId"/> is always the validated token's <c>oid</c> claim (ADR 0001).</summary>
public interface ICurrentUser
{
    /// <summary>Lowercase GUID string. Throws <see cref="InvalidOperationException"/> if the token has no valid oid.</summary>
    string UserId { get; }

    /// <summary>True when the validated token is a guest token (decided by the validating scheme, never by the id prefix).</summary>
    bool IsGuest { get; }
}
