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
/// Verifies the approved-tenant allow-list: TenantClaimsMiddleware resolves a
/// signed-in user's tenant from the token's <c>tid</c> claim against
/// <c>Tenant.AzureAdTenantId</c>. Users from approved, active tenants are scoped
/// in (auto-assigned DepartmentAdmin); users from unapproved or inactive tenants
/// get no tenant claims and are denied by default.
/// </summary>
public class TenantAllowListTests
{
    private const string ApprovedTenant1Tid = "11111111-1111-1111-1111-111111111111";
    private const string ApprovedTenant2Tid = "22222222-2222-2222-2222-222222222222";
    private const string RetiredTenantTid = "99999999-9999-9999-9999-999999999999";

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Tenants.AddRange(
            new Tenant { Id = 1, Name = "Main Hospital", AzureAdTenantId = ApprovedTenant1Tid, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = 2, Name = "North Campus", AzureAdTenantId = ApprovedTenant2Tid, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = 3, Name = "Retired Site", AzureAdTenantId = RetiredTenantTid, IsActive = false, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return db;
    }

    private static TenantClaimsMiddleware CreateMiddleware()
    {
        return new TenantClaimsMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new SuperAdminOptions()));
    }

    private static (DefaultHttpContext context, ClaimsIdentity identity) CreateContext(string tid, string? oid = null)
    {
        var claims = new List<Claim> { new("tid", tid) };
        if (oid != null)
        {
            claims.Add(new Claim("oid", oid));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, oid));
        }

        var identity = new ClaimsIdentity(claims, "test-auth");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        return (httpContext, identity);
    }

    [Fact]
    public async Task UserWithApprovedTid_IsScopedIntoTenant()
    {
        var db = CreateDbContext();
        var middleware = CreateMiddleware();
        var (context, identity) = CreateContext(ApprovedTenant1Tid, oid: "user-oid-1");

        await middleware.InvokeAsync(context, db);

        // Scoped claim + permissions granted for the approved tenant.
        context.User.HasClaim("TenantId:1", "DepartmentAdmin").Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminScoped).Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.ScheduleRead).Should().BeTrue();

        // Not scoped into the other tenant.
        context.User.HasClaim("TenantId:2", "DepartmentAdmin").Should().BeFalse();

        // Auto-assigned TenantAdmin record was persisted.
        var records = await db.TenantAdmins.Where(a => a.TenantId == 1).ToListAsync();
        records.Should().ContainSingle();
        records[0].AzureAdObjectId.Should().Be("user-oid-1");
        records[0].Role.Should().Be("DepartmentAdmin");
        records[0].IsAutoAssigned.Should().BeTrue();
    }

    [Fact]
    public async Task UserWithUnapprovedTid_GetsNoTenantClaims()
    {
        var db = CreateDbContext();
        var middleware = CreateMiddleware();
        var (context, _) = CreateContext("aaaaaaaa-0000-0000-0000-000000000000", oid: "user-oid-unknown");

        await middleware.InvokeAsync(context, db);

        context.User.Claims.Where(c => c.Type.StartsWith("TenantId:")).Should().BeEmpty();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminScoped).Should().BeFalse();

        (await db.TenantAdmins.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UserWithTidOfInactiveTenant_IsNotScopedIn()
    {
        var db = CreateDbContext();
        var middleware = CreateMiddleware();
        var (context, _) = CreateContext(RetiredTenantTid, oid: "user-oid-retired");

        await middleware.InvokeAsync(context, db);

        // Inactive tenants are not part of the allow-list.
        context.User.Claims.Where(c => c.Type.StartsWith("TenantId:")).Should().BeEmpty();
    }

    [Fact]
    public async Task TidResolution_IsIdempotent_NoDuplicateAssignments()
    {
        var db = CreateDbContext();
        var middleware = CreateMiddleware();
        var (context, _) = CreateContext(ApprovedTenant2Tid, oid: "user-oid-2");

        await middleware.InvokeAsync(context, db);
        await middleware.InvokeAsync(context, db);

        (await db.TenantAdmins.Where(a => a.AzureAdObjectId == "user-oid-2").CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SuperAdmin_BypassesAllowList()
    {
        var db = CreateDbContext();
        var middleware = new TenantClaimsMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new SuperAdminOptions { Emails = ["root@system.org"] }));
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Email, "root@system.org"), new Claim("tid", "unapproved-tenant-guid") },
            "test-auth");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        await middleware.InvokeAsync(context, db);

        // Super admin gets full access regardless of tenant allow-list.
        context.User.HasClaim(ClaimTypes.Role, "OnCall.Admin").Should().BeTrue();
        context.User.HasClaim(Permissions.ClaimType, Permissions.AdminFull).Should().BeTrue();
    }
}
