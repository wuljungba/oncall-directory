using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OnCallApi.Controllers;
using OnCallApi.Authorization;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Middleware;
using OnCallApi.Models;

namespace BackendTests.Controllers;

/// <summary>
/// Verifies the super-admin full-access feature end to end:
/// a user whose email/object ID matches <c>Authentication:SuperAdmins</c> is granted
/// every role and permission by TenantClaimsMiddleware — even if their original
/// token carried none — so protected endpoints like /api/tenants become reachable.
/// </summary>
public class SuperAdminGrantTests
{
    private const string DevEmail = "dev@local";

    /// <summary>
    /// Boots the real app (dev auth enabled) with an in-memory DB and, optionally,
    /// <c>dev@local</c> configured as a super administrator.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(bool configureSuperAdmin)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                if (configureSuperAdmin)
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Authentication:SuperAdmins:Emails:0"] = DevEmail,
                    });
                }
            });

            builder.ConfigureServices(services =>
            {
                // Swap the SQLite dev DB for an in-memory one so tests are hermetic.
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase($"superadmin-grant-{Guid.NewGuid():N}"));
            });
        });

        // Seed active tenants so the super-admin tenant-claim grant has data to expose.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.AddRange(
                new Tenant { Id = 1, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
                new Tenant { Id = 2, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        return factory;
    }

    private static HttpRequestMessage CreateRequestWithDevRole(string path, string roleCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"X-Dev-Role={roleCookie}");
        return request;
    }

    [Fact]
    public async Task ConfiguredSuperAdmin_ViewerRole_GetsFullAccess()
    {
        using var factory = CreateFactory(configureSuperAdmin: true);
        using var client = factory.CreateClient();

        // A viewer-only token would be denied /api/tenants — the super-admin
        // grant is what upgrades it to Tenant.Manage.
        using var tenantsResponse = await client.SendAsync(CreateRequestWithDevRole("/api/tenants", "viewer"));

        tenantsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // The "me" endpoint reflects the granted roles and permissions.
        using var meRequest = CreateRequestWithDevRole("/api/auth/me", "viewer");
        using var meResponse = await client.SendAsync(meRequest);
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await meResponse.Content.ReadFromJsonAsync<CurrentUserResponse>();
        me.Should().NotBeNull();
        me!.Roles.Should().Contain("OnCall.Admin");
        me.Permissions.Should().Contain(Permissions.AdminFull);
        me.Permissions.Should().Contain(Permissions.TenantManage);
    }

    [Fact]
    public async Task NonSuperAdmin_ViewerRole_IsDeniedTenantManagement()
    {
        using var factory = CreateFactory(configureSuperAdmin: false);
        using var client = factory.CreateClient();

        using var tenantsResponse = await client.SendAsync(CreateRequestWithDevRole("/api/tenants", "viewer"));

        // Without the super-admin grant, the viewer role must not reach Tenant.Manage.
        tenantsResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Unit-level check of the grant for real Entra/Google users: a principal with
    /// no app roles and no tenant claims is given every role, every permission,
    /// and SuperAdmin status on every active tenant.
    /// </summary>
    [Fact]
    public async Task GrantSuperAdmin_AddsRolesPermissionsAndTenantClaims()
    {
        var db = CreateInMemoryDbContext();
        db.Tenants.AddRange(
            new Tenant { Id = 1, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = 2, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = 3, Name = "Retired Site", IsActive = false, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Email, "real-admin@hospital.org") },
            "test-auth");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };

        var middleware = new TenantClaimsMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new SuperAdminOptions { Emails = ["real-admin@hospital.org"] }));

        await middleware.InvokeAsync(httpContext, db);

        foreach (var role in Permissions.SuperAdminRoles)
        {
            httpContext.User.HasClaim(ClaimTypes.Role, role).Should().BeTrue($"role {role} should be granted");
        }
        foreach (var perm in Permissions.SuperAdminPermissions)
        {
            httpContext.User.HasClaim(Permissions.ClaimType, perm).Should().BeTrue($"permission {perm} should be granted");
        }

        // Active tenants only — the retired tenant must be excluded.
        httpContext.User.HasClaim("TenantId:1", "SuperAdmin").Should().BeTrue();
        httpContext.User.HasClaim("TenantId:2", "SuperAdmin").Should().BeTrue();
        httpContext.User.HasClaim("TenantId:3", "SuperAdmin").Should().BeFalse();
    }

    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
