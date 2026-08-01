namespace OnCallApi.Configuration;

/// <summary>
/// Designates full-access super administrators for real (non-dev) authentication.
///
/// Real Entra/Google tokens carry no app roles, so a signed-in user can never
/// obtain Admin.Full / Tenant.Manage with the current code. A user whose email
/// address OR Entra object ID matches one of these lists is granted every role
/// and permission by <see cref="OnCallApi.Middleware.TenantClaimsMiddleware"/>.
///
/// Values are read from configuration (environment variables / Key Vault) — never
/// hardcoded. Section: "Authentication:SuperAdmins".
/// </summary>
public class SuperAdminOptions
{
    public const string SectionName = "Authentication:SuperAdmins";

    /// <summary>Email addresses granted full admin access (compared case-insensitively).</summary>
    public List<string> Emails { get; set; } = [];

    /// <summary>Entra object IDs granted full admin access.</summary>
    public List<string> ObjectIds { get; set; } = [];
}
