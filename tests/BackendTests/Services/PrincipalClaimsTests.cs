using System.Security.Claims;
using FluentAssertions;
using OnCallApi.Authorization;

namespace BackendTests.Services;

/// <summary>
/// Everything that identifies a principal must agree on the answer.
///
/// It did not: claim expansion checked only the short "oid" claim and fell through to
/// "sub", while tenant resolution also checked the namespace-qualified claim that
/// Microsoft.Identity.Web actually populates. For a real Entra token they returned
/// different values, so a tenant admin appointed under one id was invisible to the other
/// — they would hold Admin.Scoped and still resolve to no tenants, unable to administer
/// anything.
/// </summary>
public class PrincipalClaimsTests
{
    private const string LongFormOid = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void EntraToken_PrefersObjectIdOverSubject()
    {
        // Microsoft.Identity.Web maps oid onto the namespace-qualified claim, and the
        // token also carries a different, app-specific "sub". The object id must win.
        var user = Principal(
            (LongFormOid, "a7cb242c-066e-41c0-8fd3-cce9547bccc9"),
            ("sub", "c_vbdfBv28XKqEb3E7ivLIgGLwE7PYVPd"),
            (ClaimTypes.NameIdentifier, "c_vbdfBv28XKqEb3E7ivLIgGLwE7PYVPd"));

        PrincipalClaims.GetObjectId(user).Should().Be("a7cb242c-066e-41c0-8fd3-cce9547bccc9");
    }

    [Fact]
    public void ShortOidClaim_IsPreferredWhenPresent()
    {
        var user = Principal(("oid", "short-form-oid"), (LongFormOid, "long-form-oid"));

        PrincipalClaims.GetObjectId(user).Should().Be("short-form-oid");
    }

    [Fact]
    public void GoogleToken_UsesTheMappedGooglePrefixedObjectId()
    {
        // The Google handler adds oid = "google-{sub}" during token validation.
        var user = Principal(("oid", "google-12345"), ("sub", "12345"));

        PrincipalClaims.GetObjectId(user).Should().Be("google-12345");
    }

    [Fact]
    public void FallsBackToSubjectThenNameIdentifier()
    {
        PrincipalClaims.GetObjectId(Principal(("sub", "subject-only"))).Should().Be("subject-only");
        PrincipalClaims.GetObjectId(Principal((ClaimTypes.NameIdentifier, "nameid-only"))).Should().Be("nameid-only");
        PrincipalClaims.GetObjectId(Principal()).Should().BeNull();
    }

    [Theory]
    [InlineData(ClaimTypes.Email)]
    [InlineData("email")]
    [InlineData("preferred_username")]
    [InlineData("upn")]
    [InlineData(ClaimTypes.Upn)]
    public void EmailIsResolvedFromAnyOfTheUsualClaims(string claimType)
    {
        PrincipalClaims.GetEmail(Principal((claimType, "user@example.test")))
            .Should().Be("user@example.test");
    }

    [Fact]
    public void AV1TokenCarryingOnlyUpnStillResolves()
    {
        // The case that was silently broken. This API issues v1 access tokens, which carry
        // upn and never preferred_username, and a cloud-only Entra user with no mailbox has
        // mail: null so no email claim is issued either. Every branch missed, the resolver
        // returned null, and an email-keyed grant could not match — so the person signed in
        // and landed with no permissions at all.
        var user = Principal(
            ("oid", "a7cb242c-066e-41c0-8fd3-cce9547bccc9"),
            ("tid", "e9577a81-c4d6-4124-984f-78f3c0efcaf4"),
            ("upn", "clinician@hospital.test"));

        PrincipalClaims.GetEmail(user).Should().Be("clinician@hospital.test");
    }

    [Fact]
    public void AnExplicitEmailStillWinsOverUpn()
    {
        // upn is a last resort: adding it must not change any principal that already resolved.
        var user = Principal(
            ("email", "real@hospital.test"),
            ("upn", "different@hospital.test"));

        PrincipalClaims.GetEmail(user).Should().Be("real@hospital.test");
    }

    [Fact]
    public void PreferredUsernameStillWinsOverUpn()
    {
        var user = Principal(
            ("preferred_username", "real@hospital.test"),
            ("upn", "different@hospital.test"));

        PrincipalClaims.GetEmail(user).Should().Be("real@hospital.test");
    }

    [Fact]
    public void AGuestUpnIsNotAnAddress()
    {
        // A B2B guest's UPN is a mangled internal form. It contains an "@", so every caller
        // would take it for an address — but nobody could ever have granted access to that
        // string, so resolving to it would read as "no access" just as the missing claim did.
        var user = Principal(("upn", "someone_gmail.com#EXT#@tenant.onmicrosoft.com"));

        PrincipalClaims.GetEmail(user).Should().BeNull();
    }

    // ── Display name: who a record gets attributed to ───────────────────────────────────

    /// <summary>
    /// The case that was broken in production.
    ///
    /// Starting a code call recorded the operator with User.Identity?.Name alone.
    /// Microsoft.Identity.Web's default name claim is preferred_username, which is a v2
    /// claim, and this API issues v1 tokens — so on the Entra path the name was null and
    /// every code call raised was attributed to nobody at all.
    /// </summary>
    [Fact]
    public void AV1EntraTokenStillYieldsAName()
    {
        var user = Principal(
            (LongFormOid, "a7cb242c-066e-41c0-8fd3-cce9547bccc9"),
            ("upn", "divine@hospital.test"));

        PrincipalClaims.GetDisplayName(user).Should().Be("divine@hospital.test");
    }

    [Fact]
    public void APresentDisplayNameIsPreferredOverTheAddress()
    {
        var user = Principal(
            (ClaimTypes.Name, "Divine Yisa"),
            (ClaimTypes.Email, "divine@hospital.test"));

        PrincipalClaims.GetDisplayName(user).Should().Be("Divine Yisa");
    }

    [Fact]
    public void TheShortNameClaimIsAcceptedToo()
    {
        PrincipalClaims.GetDisplayName(Principal(("name", "Charge Nurse"))).Should().Be("Charge Nurse");
    }

    /// <summary>A blank name must not beat a usable address.</summary>
    [Fact]
    public void AWhitespaceNameFallsThroughToTheAddress()
    {
        var user = Principal((ClaimTypes.Name, "   "), ("email", "nurse@hospital.test"));

        PrincipalClaims.GetDisplayName(user).Should().Be("nurse@hospital.test");
    }

    /// <summary>
    /// Nothing identifiable is null, not a placeholder. A caller that records "unknown" as if
    /// it were a name produces a record that looks attributed and is not.
    /// </summary>
    [Fact]
    public void AnUnidentifiablePrincipalYieldsNull()
    {
        PrincipalClaims.GetDisplayName(Principal(("sub", "abc123"))).Should().BeNull();
    }

    /// <summary>A guest's mangled UPN is not an address, so it is not a name either.</summary>
    [Fact]
    public void AGuestUpnIsNotUsedAsAName()
    {
        var user = Principal(("upn", "someone_gmail.com#EXT#@tenant.onmicrosoft.com"));

        PrincipalClaims.GetDisplayName(user).Should().BeNull();
    }

    [Fact]
    public void TenantIdIsResolvedFromEitherForm()
    {
        PrincipalClaims.GetTenantId(Principal(("tid", "tenant-guid"))).Should().Be("tenant-guid");
        PrincipalClaims.GetTenantId(Principal(
            ("http://schemas.microsoft.com/identity/claims/tenantid", "tenant-guid")))
            .Should().Be("tenant-guid");
    }
}
