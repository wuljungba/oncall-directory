using System.Security.Claims;

namespace OnCallApi.Authorization;

/// <summary>
/// The single place that decides "who is this principal?".
///
/// This existed in two copies with different fallback orders, and they disagreed:
/// Microsoft.Identity.Web maps the Entra <c>oid</c> onto the namespace-qualified claim, so
/// a resolver that checked only the short <c>"oid"</c> fell through to <c>sub</c> — a
/// different, app-specific identifier. The result was that claim expansion looked up a
/// tenant admin by <c>sub</c> while tenant resolution looked them up by <c>oid</c>, so an
/// appointed sub-admin could hold Admin.Scoped and still resolve to no tenants at all.
/// </summary>
public static class PrincipalClaims
{
    /// <summary>
    /// The stable identifier for a principal: the Entra object id where there is one
    /// (short or namespace-qualified form), otherwise the provider's subject. Google
    /// tokens are given <c>oid</c> = "google-{sub}" when validated, so they land here too.
    /// </summary>
    public static string? GetObjectId(ClaimsPrincipal user)
    {
        return user.FindFirst("oid")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>
    /// The principal's email. Permission grants may be keyed on this rather than an object
    /// id, so it must resolve identically wherever grants are matched.
    /// </summary>
    public static string? GetEmail(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst("preferred_username")?.Value;
    }

    /// <summary>The token's home tenant, when it carries one.</summary>
    public static string? GetTenantId(ClaimsPrincipal user)
    {
        return user.FindFirst("tid")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
    }
}
