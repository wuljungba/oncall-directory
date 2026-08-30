using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OnCallApi.Authorization;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Middleware;
using OnCallApi.Models;

namespace BackendTests.Services;

/// <summary>
/// Access is invitation-only.
///
/// Matching the token's <c>tid</c> against <c>Tenant.AzureAdTenantId</c> used to
/// auto-create a DepartmentAdmin row and grant <c>ScopedAdminPermissions</c> — which
/// includes <c>CodeCall.Write</c>. Every employee in the hospital's Entra tenant therefore
/// became a department admin able to fire a live code call on their first sign-in, with no
/// invitation and no approval.
///
/// A matching tenant now confers <c>ConnectedTenantPermissions</c> — Schedule.Read and
/// Directory.Read, nothing else — because an administrator has to put that Entra tenant
/// GUID on that subscription, which is the approval. These tests pin the boundary: read
/// yes, write no, admin no, and no row written on anyone's behalf. Anything beyond reading
/// still comes only from an explicit grant, a tenant-admin record, a mapped Entra group,
/// or super-admin configuration.
/// </summary>
public class TenantAllowListTests
{
    private const string ApprovedTenant1Tid = "11111111-1111-1111-1111-111111111111";
    private const string ApprovedTenant2Tid = "22222222-2222-2222-2222-222222222222";
    private const string RetiredTenantTid = "99999999-9999-9999-9999-999999999999";
    private const string MappedGroupId = "group-aaaa-bbbb";

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Tenants.AddRange(
            new Tenant { Id = 1, Name = "Main Hospital", AzureAdTenantId = ApprovedTenant1Tid, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = 2, Name = "North Campus", AzureAdTenantId = ApprovedTenant2Tid, AzureAdGroupId = MappedGroupId, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = 3, Name = "Retired Site", AzureAdTenantId = RetiredTenantTid, IsActive = false, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return db;
    }

    private static TenantClaimsMiddleware CreateMiddleware(params string[] superAdminEmails)
    {
        return new TenantClaimsMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new SuperAdminOptions { Emails = [.. superAdminEmails] }));
    }

    private static DefaultHttpContext CreateContext(string? tid, string? oid = null, string? groupId = null, string? email = null)
    {
        var claims = new List<Claim>();
        if (tid != null) claims.Add(new Claim("tid", tid));
        if (email != null) claims.Add(new Claim(ClaimTypes.Email, email));
        if (groupId != null) claims.Add(new Claim("groups", groupId));
        if (oid != null)
        {
            claims.Add(new Claim("oid", oid));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, oid));
        }

        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test-auth")),
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
    }

    [Fact]
    public async Task MatchingTenantId_GrantsReadOnly_AndNothingElse()
    {
        var db = CreateDbContext();
        var context = CreateContext(ApprovedTenant1Tid, oid: "user-oid-1");

        await CreateMiddleware().InvokeAsync(context, db);

        // Scoped to the subscription whose directory issued the token, and able to read it.
        context.User.HasClaim("TenantId:1", TenantClaimsMiddleware.ConnectedTenantRole).Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.ScheduleRead).Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.DirectoryRead).Should().BeTrue();

        // The line this must never cross again. CodeCall.Write is the right to page
        // on-call clinicians for a real emergency; it is not something to acquire by
        // holding a token from the right directory.
        context.User.HasClaim(Permissions.ClaimType, Permissions.CodeCallWrite).Should().BeFalse();
        context.User.HasClaim(Permissions.ClaimType, Permissions.ScheduleWrite).Should().BeFalse();
        context.User.HasClaim(Permissions.ClaimType, Permissions.DirectoryWrite).Should().BeFalse();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminScoped).Should().BeFalse();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminFull).Should().BeFalse();

        // And no admin record is conjured on their behalf. The claim lasts one request;
        // a row would outlive the connection that justified it.
        (await db.TenantAdmins.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MatchingTenantId_DoesNotReachOtherTenants()
    {
        var db = CreateDbContext();
        var context = CreateContext(ApprovedTenant1Tid, oid: "user-oid-1");

        await CreateMiddleware().InvokeAsync(context, db);

        // Tenant 2 is equally connected, to a different directory. This token is not from it.
        context.User.Claims.Where(c => c.Type.StartsWith("TenantId:"))
            .Select(c => c.Type).Should().BeEquivalentTo(["TenantId:1"]);
    }

    [Fact]
    public async Task ConnectedDirectory_DoesNotOverrideAnAdminRecord()
    {
        // A real appointment in the same tenant must keep its stronger claims rather than
        // being flattened to read-only by the directory match.
        var db = CreateDbContext();
        db.TenantAdmins.Add(new TenantAdmin
        {
            TenantId = 1,
            AzureAdObjectId = "appointed",
            Role = "DepartmentAdmin",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var context = CreateContext(ApprovedTenant1Tid, oid: "appointed");

        await CreateMiddleware().InvokeAsync(context, db);

        context.User.HasClaim("TenantId:1", "DepartmentAdmin").Should().BeTrue();
        context.User.HasClaim("TenantId:1", TenantClaimsMiddleware.ConnectedTenantRole).Should().BeFalse();
        context.User.HasClaim(Permissions.ClaimType, Permissions.CodeCallWrite).Should().BeTrue();
    }

    [Fact]
    public async Task UnapprovedTenantId_ConfersNothing()
    {
        var db = CreateDbContext();
        var context = CreateContext("aaaaaaaa-0000-0000-0000-000000000000", oid: "user-oid-unknown");

        await CreateMiddleware().InvokeAsync(context, db);

        context.User.Claims.Where(c => c.Type.StartsWith("TenantId:")).Should().BeEmpty();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminScoped).Should().BeFalse();
        (await db.TenantAdmins.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExistingTenantAdminRecord_StillGrantsScopedAccess()
    {
        // The supported route in: an administrator appointed them.
        var db = CreateDbContext();
        db.TenantAdmins.Add(new TenantAdmin
        {
            TenantId = 1,
            AzureAdObjectId = "invited-user",
            Role = "DepartmentAdmin",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var context = CreateContext(ApprovedTenant1Tid, oid: "invited-user");

        await CreateMiddleware().InvokeAsync(context, db);

        context.User.HasClaim("TenantId:1", "DepartmentAdmin").Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminScoped).Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.ScheduleRead).Should().BeTrue();
    }

    [Fact]
    public async Task MappedEntraGroup_StillAutoAssigns()
    {
        // Group mapping is retained: an administrator maps a specific group to a tenant and
        // someone must add the user to it, which is an invitation.
        var db = CreateDbContext();
        var context = CreateContext(ApprovedTenant2Tid, oid: "group-member", groupId: MappedGroupId);

        await CreateMiddleware().InvokeAsync(context, db);

        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminScoped).Should().BeTrue();

        var records = await db.TenantAdmins.Where(a => a.TenantId == 2).ToListAsync();
        records.Should().ContainSingle();
        records[0].AzureAdObjectId.Should().Be("group-member");
        records[0].IsAutoAssigned.Should().BeTrue();
    }

    [Fact]
    public async Task UnmappedGroupMembership_ConfersNothing()
    {
        var db = CreateDbContext();
        var context = CreateContext(ApprovedTenant2Tid, oid: "other-group-member", groupId: "some-other-group");

        await CreateMiddleware().InvokeAsync(context, db);

        // Their directory is connected, so read-only scoping is expected and correct.
        // What being in an unmapped group must not do is auto-assign them as an admin.
        context.User.HasClaim("TenantId:2", TenantClaimsMiddleware.ConnectedTenantRole).Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminScoped).Should().BeFalse();
        context.User.HasClaim(Permissions.ClaimType, Permissions.CodeCallWrite).Should().BeFalse();
        (await db.TenantAdmins.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task InactiveTenant_ConfersNothing()
    {
        var db = CreateDbContext();
        var context = CreateContext(RetiredTenantTid, oid: "user-oid-retired");

        await CreateMiddleware().InvokeAsync(context, db);

        context.User.Claims.Where(c => c.Type.StartsWith("TenantId:")).Should().BeEmpty();
    }

    [Fact]
    public async Task GroupAutoAssignment_IsIdempotent()
    {
        var db = CreateDbContext();
        var context = CreateContext(ApprovedTenant2Tid, oid: "group-member", groupId: MappedGroupId);

        await CreateMiddleware().InvokeAsync(context, db);
        await CreateMiddleware().InvokeAsync(context, db);

        (await db.TenantAdmins.Where(a => a.AzureAdObjectId == "group-member").CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SuperAdmin_IsUnaffected()
    {
        var db = CreateDbContext();
        var context = CreateContext("unapproved-tenant-guid", email: "root@system.org");

        await CreateMiddleware("root@system.org").InvokeAsync(context, db);

        context.User.HasClaim(ClaimTypes.Role, "OnCall.Admin").Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminFull).Should().BeTrue();
    }
}
