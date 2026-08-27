using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Authentication;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Controllers;

/// <summary>
/// The boundaries between the four access concepts — configured super admin, Admin.Full,
/// tenant (called a "subscription" in the UI), and tenant sub-admin — where they had no
/// coverage.
///
/// Existing suites already cover what each persona can SEE (TenantScopingTests,
/// AdminServiceTenantScopingTests) and what a sub-admin may GRANT (DelegatedGrantTests).
/// These cover how a principal BECOMES one of those personas, which is where the remaining
/// escalation paths were.
/// </summary>
[Collection(WebHostCollection.Name)]
public class PermissionModelTests
{
    private const string SigningKey = "test-signing-key-for-permission-model-0123456789";
    private const string SuperAdminEmail = "boss@hospital.test";

    private static WebApplicationFactory<Program> CreateFactory(string dbName)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // Must be UseSetting, not ConfigureAppConfiguration: Program.cs reads DevAuth
            // eagerly, and with it left on every "expect denied" assertion passes vacuously.
            builder.UseSetting("DevAuth:Enabled", "false");
            builder.UseSetting("Authentication:Local:SigningKey", SigningKey);
            builder.UseSetting("Authentication:SuperAdmins:Emails:0", SuperAdminEmail);
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });
    }

    private static async Task<List<string>> PermissionsFor(
        WebApplicationFactory<Program> factory, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        return body?.Permissions ?? [];
    }

    private sealed class MeResponse
    {
        public List<string> Permissions { get; set; } = [];
    }

    // ── Configured super admin vs local accounts ──────────────────────────────

    /// <summary>
    /// A super administrator is identified by EMAIL, and a local account's email is chosen by
    /// whoever creates it and verified by nothing. Registration must refuse the address.
    /// </summary>
    [Fact]
    public async Task LocalAccount_CannotBeRegistered_WithAConfiguredSuperAdminEmail()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");
        using var scope = factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<ILocalAccountService>();

        var act = async () => await accounts.RegisterAsync(
            SuperAdminEmail, "an-adequately-long-password", "Impostor");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reserved*");
    }

    /// <summary>Case and surrounding whitespace must not get past the same guard.</summary>
    [Theory]
    [InlineData("BOSS@HOSPITAL.TEST")]
    [InlineData("  boss@hospital.test  ")]
    public async Task LocalAccount_SuperAdminEmailGuard_IgnoresCaseAndWhitespace(string variant)
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");
        using var scope = factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<ILocalAccountService>();

        var act = async () => await accounts.RegisterAsync(variant, "an-adequately-long-password", "Impostor");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The counterpart of the guard above, kept explicit so nobody "hardens" it away: a local
    /// principal bearing a configured super admin's address IS elevated, because an operator
    /// may deliberately configure a local break-glass super admin. That is safe only because
    /// the address cannot be claimed — RegisterAsync refuses it, and UpdateAsync exposes no
    /// way to change an existing account's email.
    /// </summary>
    [Fact]
    public async Task ConfiguredSuperAdminEmail_IsHonoured_EvenForALocalPrincipal()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(7, SuperAdminEmail, "The Boss", new[] { "OnCall.Viewer" });
        }

        var permissions = await PermissionsFor(factory, token);

        permissions.Should().Contain(Permissions.AdminFull);
        permissions.Should().Contain(Permissions.TenantManage);
    }

    /// <summary>Duplicate detection must survive case and whitespace too.</summary>
    [Fact]
    public async Task LocalAccount_DuplicateEmail_IsRejectedRegardlessOfCasing()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");
        using var scope = factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<ILocalAccountService>();

        await accounts.RegisterAsync("nurse@hospital.test", "an-adequately-long-password", "Nurse");

        var act = async () => await accounts.RegisterAsync(
            "  Nurse@Hospital.TEST  ", "an-adequately-long-password", "Nurse Again");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    // ── Permission grants: object id vs email ─────────────────────────────────

    /// <summary>Granting against an address is the documented pre-sign-in onboarding flow.</summary>
    [Fact]
    public async Task GrantKeyedToEmail_IsHonouredForThatAddress()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");
        SeedGrant(factory, "clinician@hospital.test", Permissions.ScheduleRead);

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(11, "clinician@hospital.test", "Clinician", new[] { "OnCall.Viewer" });
        }

        (await PermissionsFor(factory, token)).Should().Contain(Permissions.ScheduleRead);
    }

    /// <summary>
    /// A grant keyed to an object id must not be satisfied by an email claim carrying the
    /// same text, and vice versa — the two identifier spaces stay separate.
    /// </summary>
    [Fact]
    public async Task GrantKeyedToObjectId_IsNotSatisfiedByAMatchingEmailClaim()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");

        // A local principal's oid is "local-{id}". A grant recorded against that exact string
        // is an object-id grant, so an email claim must never satisfy it.
        SeedGrant(factory, "local-12", Permissions.DirectoryWrite);

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            // Different user id, but an email spelled exactly like the granted object id.
            token = jwt.GenerateToken(99, "local-12", "Lookalike", new[] { "OnCall.Viewer" });
        }

        (await PermissionsFor(factory, token)).Should().NotContain(Permissions.DirectoryWrite);
    }

    /// <summary>The floor: no grant, no tenant admin row, not configured — nothing at all.</summary>
    [Fact]
    public async Task PrincipalWithNoGrantAndNoAdminRow_HasNoPermissions()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(123, "stranger@hospital.test", "Stranger", new[] { "OnCall.Viewer" });
        }

        (await PermissionsFor(factory, token)).Should().BeEmpty(
            "a role claim alone must never confer permissions");
    }

    // ── Entra group auto-assignment ───────────────────────────────────────────
    //
    // Matching a Tenant.AzureAdGroupId auto-creates a TenantAdmin row and grants
    // ScopedAdminPermissions — including CodeCall.Write, the right to fire a live code call.
    // It is a deliberate invitation mechanism, but it had no coverage at all, and it is the
    // same shape as the `tid` flaw that once made every employee a department admin.

    /// <summary>A group an administrator deliberately mapped does confer scoped admin.</summary>
    [Fact]
    public async Task GroupAutoAssign_MatchingGroup_CreatesTenantAdminAndGrantsScopedPermissions()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");
        SeedTenant(factory, id: 1, groupId: "group-cardiology", isActive: true);

        var token = TokenWithGroups(factory, "oid-alice", "alice@hospital.test", "group-cardiology");
        var permissions = await PermissionsFor(factory, token);

        permissions.Should().Contain(Permissions.AdminScoped);
        permissions.Should().Contain(Permissions.DirectoryWrite);

        // Deliberately NOT CodeCall.Write. This path grants access because IT added someone
        // to a directory group, with nobody reviewing the individual; firing a live code
        // call pages on-call clinicians for a real emergency and needs an explicit grant.
        permissions.Should().NotContain(Permissions.CodeCallWrite,
            "group membership must not confer the right to page clinicians");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TenantAdmins.Should().ContainSingle(a => a.AzureAdObjectId == "oid-alice" && a.TenantId == 1);
    }

    /// <summary>
    /// A tenant with a blank group mapping is not a mapping. Matching on it would hand out
    /// CodeCall.Write on an accident of data entry.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GroupAutoAssign_BlankGroupMapping_GrantsNothing(string groupId)
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");
        SeedTenant(factory, id: 1, groupId: groupId, isActive: true);

        // A principal whose own group claim is equally blank — the collision being guarded.
        var token = TokenWithGroups(factory, "oid-bob", "bob@hospital.test", groupId);

        (await PermissionsFor(factory, token)).Should().BeEmpty();
    }

    /// <summary>Deactivating a subscription must stop conferring access through its group.</summary>
    [Fact]
    public async Task GroupAutoAssign_InactiveTenant_GrantsNothing()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");
        SeedTenant(factory, id: 1, groupId: "group-retired", isActive: false);

        var token = TokenWithGroups(factory, "oid-carol", "carol@hospital.test", "group-retired");

        (await PermissionsFor(factory, token)).Should().BeEmpty();
    }

    /// <summary>A group nobody mapped confers nothing.</summary>
    [Fact]
    public async Task GroupAutoAssign_UnmappedGroup_GrantsNothing()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");
        SeedTenant(factory, id: 1, groupId: "group-cardiology", isActive: true);

        var token = TokenWithGroups(factory, "oid-dave", "dave@hospital.test", "group-catering");

        (await PermissionsFor(factory, token)).Should().BeEmpty();
    }

    private static void SeedTenant(WebApplicationFactory<Program> factory, int id, string groupId, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(new Tenant
        {
            Id = id,
            Name = $"Tenant {id}",
            AzureAdGroupId = groupId,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>
    /// LocalJwtService emits no "groups" claim, so the token is minted directly against the
    /// same key/issuer/audience the local scheme validates.
    /// </summary>
    private static string TokenWithGroups(
        WebApplicationFactory<Program> factory, string objectId, string email, params string[] groups)
    {
        _ = factory;
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, objectId),
            new(System.Security.Claims.ClaimTypes.Email, email),
            new(System.Security.Claims.ClaimTypes.Name, "Group Member"),
            new("oid", objectId),
            new("scp", "access_as_user"),
        };
        claims.AddRange(groups.Select(g => new System.Security.Claims.Claim("groups", g)));

        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(SigningKey));
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: LocalJwtService.Issuer,
            audience: LocalJwtService.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private static void SeedGrant(WebApplicationFactory<Program> factory, string principalId, string permission)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PermissionGrants.Add(new PermissionGrant
        {
            PrincipalType = "local",
            ExternalPrincipalId = principalId,
            Permissions = permission,
            IsActive = true,
        });
        db.SaveChanges();
    }

    // ── Role names confer nothing on a real token ──────────────────────────────
    // Permissions.DevRoleToPermissions maps "OnCall.Admin" to Admin.Full + Tenant.Manage,
    // but it exists solely for the dev-auth handler. The role-claim policies were removed
    // as "a weaker parallel authorization path", and this pins that: if anyone reconnects
    // role expansion to real tokens, one app role would silently own every tenant's data.

    [Fact]
    public async Task AnAdminRoleClaimOnARealTokenGrantsNoAdminPermissions()
    {
        var factory = CreateFactory($"perm-model-{Guid.NewGuid():N}");

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(
                31, "roleclaimer@hospital.test", "Role Claimer", new[] { "OnCall.Admin" });
        }

        var permissions = await PermissionsFor(factory, token);

        permissions.Should().NotContain(Permissions.AdminFull);
        permissions.Should().NotContain(Permissions.TenantManage);
        permissions.Should().NotContain(Permissions.AdminScoped);
        permissions.Should().BeEmpty(
            "a role name in a token is not an authorization decision; permissions come from "
            + "configured super admins, TenantAdmin rows or PermissionGrant rows");
    }

    /// <summary>
    /// The grant UI must never be able to mint an administrator. AssignablePermissions
    /// excludes the admin permissions and ParseAssignablePermissionCsv enforces it; this
    /// pins the end-to-end result rather than the constant.
    /// </summary>
    [Fact]
    public void AdminPermissionsAreNotAssignableThroughAGrant()
    {
        var parsed = Permissions.ParseAssignablePermissionCsv(
            $"{Permissions.AdminFull},{Permissions.TenantManage},{Permissions.AdminScoped},{Permissions.ScheduleRead}");

        parsed.Should().BeEquivalentTo(new[] { Permissions.ScheduleRead },
            "only the non-admin permissions may be handed out by an admin through the dashboard");
    }
}
