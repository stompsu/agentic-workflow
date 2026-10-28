namespace AgenticWorkflow.Api.Guests;

/// <summary>Scheme names and the discriminator constant (ADR 0006 section 5.3).</summary>
public static class GuestAuth
{
    /// <summary>The default (policy) scheme: routes by the unvalidated issuer. Routing only, never authorization.</summary>
    public const string ApiBearerScheme = "ApiBearer";

    /// <summary>The JwtBearer scheme for guest tokens (registered only when Guest:Enabled).</summary>
    public const string Scheme = "Guest";

    /// <summary>Stamped as ClaimsIdentity.AuthenticationType by the Guest scheme's validation parameters, never by token content.</summary>
    public const string AuthenticationType = "guest";

    public const string Algorithm = "ES256";
}
