namespace OnCallApi.Authentication;

/// <summary>
/// Configuration for validating Google-issued JWTs.
/// Google tokens are validated against their public JWKS endpoint and
/// standard OpenID Connect rules.
/// </summary>
public class GoogleTokenValidationOptions
{
    public const string SectionName = "Authentication:Google";

    /// <summary>The Google OAuth 2.0 client ID (audience expected in the token).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Optional: restrict to specific Google Workspace domains (hd claim).</summary>
    public string[]? AuthorizedDomains { get; set; }

    /// <summary>Google's OIDC issuer.</summary>
    public const string Issuer = "https://accounts.google.com";

    /// <summary>JWKS endpoint for resolving Google's signing keys.</summary>
    public const string JwksUrl = "https://www.googleapis.com/oauth2/v3/certs";
}
