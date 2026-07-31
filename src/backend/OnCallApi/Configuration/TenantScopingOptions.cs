namespace OnCallApi.Configuration;

/// <summary>
/// Configuration options for tenant scoping behavior in <see cref="Middleware.TenantClaimsMiddleware"/>.
/// </summary>
public class TenantScopingOptions
{
    public const string SectionName = "TenantScoping";

    /// <summary>
    /// When true, requests that cannot load tenant scoping (e.g., database not ready,
    /// migrations not applied) will receive a 403 Forbidden response instead of continuing
    /// silently. Default: false — graceful degradation for zero-downtime deployments.
    ///
    /// Only set this to true after confirming migrations have been applied in production,
    /// to ensure tenant isolation is always enforced.
    /// </summary>
    public bool Required { get; set; } = false;
}
