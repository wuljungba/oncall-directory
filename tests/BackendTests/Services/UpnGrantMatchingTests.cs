using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// A Microsoft user at an organisation whose directory is NOT connected reaches their data
/// through exactly one route: a permission grant keyed to their address. This pins that the
/// route works for the token such a user actually presents.
///
/// It did not. The API issues v1 access tokens, which carry <c>upn</c> and never
/// <c>preferred_username</c>, and a cloud-only Entra user with no mailbox has
/// <c>mail: null</c> so no <c>email</c> claim either. Nothing read <c>upn</c>, so the address
/// resolved to null, the grant could not match, and the person signed in successfully with no
/// permissions — indistinguishable from being locked out.
/// </summary>
public class UpnGrantMatchingTests
{
    private const string Address = "clinician@hospital.test";

    private static AppDbContext NewDb(string? connectedDirectory = null)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        // AzureAdTenantId left null on purpose: with no connected directory the tid path
        // contributes nothing, so a tenant that comes back can only have come from the grant.
        db.Tenants.Add(new Tenant
        {
            Id = 1,
            Name = "Unsynced Hospital",
            IsActive = true,
            AzureAdTenantId = connectedDirectory,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return db;
    }

    private static void GrantTo(AppDbContext db, string principal)
    {
        db.PermissionGrants.Add(new PermissionGrant
        {
            TenantId = 1,
            PrincipalType = "external",
            ExternalPrincipalId = principal,
            Permissions = "Schedule.Read,Directory.Read",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static ClaimsPrincipal V1Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static TenantContextService Service(AppDbContext db) =>
        new(db, new Mock<IHttpContextAccessor>().Object);

    [Fact]
    public async Task AUserWhoseTokenCarriesOnlyUpn_ReachesTheirGrantedTenant()
    {
        using var db = NewDb();
        GrantTo(db, Address);

        // Exactly what a v1 token for a mailbox-less cloud user looks like: an object id,
        // a home tenant nobody has connected, and a upn. No email, no preferred_username.
        var user = V1Token(
            ("oid", "a7cb242c-066e-41c0-8fd3-cce9547bccc9"),
            ("tid", "11111111-2222-3333-4444-555555555555"),
            ("upn", Address));

        (await Service(db).GetAuthorizedTenantIdsAsync(user)).Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public async Task TheSameUserWithNoGrantStillReachesNothing()
    {
        // The fix widens how an address is read, not who may hold one. An unconnected
        // directory plus no grant is still no access.
        using var db = NewDb();

        var user = V1Token(
            ("oid", "a7cb242c-066e-41c0-8fd3-cce9547bccc9"),
            ("tid", "11111111-2222-3333-4444-555555555555"),
            ("upn", Address));

        (await Service(db).GetAuthorizedTenantIdsAsync(user)).Should().BeEmpty();
    }

    [Fact]
    public async Task AGuestUpnDoesNotMatchAGrantForTheMangledString()
    {
        // Nobody would grant to the #EXT# form, and treating it as an address would key the
        // principal to a string no administrator can type.
        using var db = NewDb();
        GrantTo(db, "someone_gmail.com#EXT#@tenant.onmicrosoft.com");

        var user = V1Token(
            ("oid", "b8dc353d-177f-52d1-9ge4-ddf0658cdd0a"),
            ("upn", "someone_gmail.com#EXT#@tenant.onmicrosoft.com"));

        (await Service(db).GetAuthorizedTenantIdsAsync(user)).Should().BeEmpty();
    }
}
