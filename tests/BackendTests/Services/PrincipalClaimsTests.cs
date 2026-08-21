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
    public void EmailIsResolvedFromAnyOfTheUsualClaims(string claimType)
    {
        PrincipalClaims.GetEmail(Principal((claimType, "user@example.test")))
            .Should().Be("user@example.test");
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
