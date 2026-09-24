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
    ///
    /// <c>upn</c> is the last resort and it is load-bearing: this API issues v1 access tokens
    /// (the app registration leaves <c>requestedAccessTokenVersion</c> unset), and a v1 token
    /// carries <c>upn</c> but never <c>preferred_username</c>, which is a v2 claim. A
    /// cloud-only Entra user with no mailbox also has <c>mail: null</c>, so no <c>email</c>
    /// claim is issued either. Without this branch every option missed and the resolver
    /// returned null, so an email-keyed grant could not match: the person signed in
    /// successfully and landed with no permissions at all. That is the same failure commit
    /// 425cb0d found for configured super admins and worked around there by matching on
    /// object ids; grants had no such escape.
    ///
    /// It is also the sounder claim to trust. A UPN requires a domain verified in the issuing
    /// tenant, whereas the <c>email</c> claim carries no such guarantee — which matters when
    /// tokens are accepted from any Entra tenant.
    /// </summary>
    public static string? GetEmail(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? UsableUpn(user.FindFirst("upn")?.Value)
            ?? UsableUpn(user.FindFirst(ClaimTypes.Upn)?.Value);
    }

    /// <summary>
    /// A human-readable name for the principal, for attributing a record to the person who
    /// created it.
    ///
    /// The fallback chain is the point. Two paths captured the acting user with two different
    /// implementations: a debrief note used a three-claim chain and worked, while starting a
    /// code call used <c>User.Identity?.Name</c> alone and did not. Microsoft.Identity.Web's
    /// default name claim is <c>preferred_username</c>, which is a v2 claim, and this API
    /// issues v1 tokens (see <see cref="GetEmail"/>) — so on the Entra path
    /// <c>Identity.Name</c> is null and the code call recorded no operator at all. An
    /// emergency page with nobody's name against it is not a record anyone can review.
    ///
    /// Falls through to the email last: an address is a worse label than a display name but a
    /// far better one than nothing, and it is the claim most likely to survive.
    /// </summary>
    public static string? GetDisplayName(ClaimsPrincipal user)
    {
        var name = user.FindFirst(ClaimTypes.Name)?.Value
            ?? user.FindFirst("name")?.Value
            ?? user.FindFirst("preferred_username")?.Value;

        return string.IsNullOrWhiteSpace(name) ? GetEmail(user) : name;
    }

    /// <summary>
    /// A UPN only when it is actually an address for this person.
    ///
    /// A B2B guest's UPN is the mangled internal form
    /// <c>someone_gmail.com#EXT#@tenant.onmicrosoft.com</c> — it contains an "@", so every
    /// caller would treat it as an address, but it is not one and it is not what any
    /// administrator would have typed when granting access. Resolving to it would key a
    /// principal to a string nobody can grant to, which reads as "no access" in exactly the
    /// same way the missing claim did.
    /// </summary>
    private static string? UsableUpn(string? upn) =>
        string.IsNullOrWhiteSpace(upn) || upn.Contains("#EXT#", StringComparison.OrdinalIgnoreCase)
            ? null
            : upn;

    /// <summary>
    /// Whether a stored directory value could actually key a working permission grant.
    ///
    /// Grants hold either an email or an object id in one column, and
    /// <see cref="Middleware.TenantClaimsMiddleware"/> tells them apart purely by whether the
    /// string contains "@". So a value with an "@" is an address, not an object id, however
    /// it reached us.
    ///
    /// The importer synthesises "csv-import-{guid}" for a row that arrives with no object id
    /// (<see cref="Services.BulkImportService"/>), and no token will ever present one. Such a
    /// value can only ever match a grant that does nothing — which matters when revoking,
    /// because a match on it would be reported as access removed when none existed.
    /// </summary>
    public static bool IsDirectoryObjectId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains('@')
        && !value.StartsWith("csv-import-", StringComparison.Ordinal);

    /// <summary>The token's home tenant, when it carries one.</summary>
    public static string? GetTenantId(ClaimsPrincipal user)
    {
        return user.FindFirst("tid")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
    }
}
