using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Authentication;
using OnCallApi.Data;
using OnCallApi.Models;

namespace BackendTests.Controllers;

/// <summary>
/// Tenant scoping must fail CLOSED.
///
/// It previously failed open: the filter was skipped entirely when a user resolved to no
/// tenants, so anyone holding a permission grant — who by definition has no TenantAdmin
/// row — saw every tenant's data. That made delegated granting unsafe, because a
/// department admin could hand out cross-tenant visibility without realising it.
/// </summary>
[Collection(WebHostCollection.Name)]
public class TenantScopingTests
{
    private const string SigningKey = "test-signing-key-for-tenant-scoping-0123456789";
    private const int TenantA = 1;
    private const int TenantB = 2;

    private static WebApplicationFactory<Program> CreateFactory(string dbName)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("DevAuth:Enabled", "false");
            builder.UseSetting("Authentication:Local:SigningKey", SigningKey);
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });
    }

    /// <summary>Two tenants, one department each, plus an optional grant for user 42.</summary>
    private static WebApplicationFactory<Program> Seed(int? grantTenantId, bool grantSystemWide = false, bool noGrant = false)
    {
        var factory = CreateFactory($"tenant-scoping-{Guid.NewGuid():N}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = TenantB, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.Departments.AddRange(
            new Department { Id = 10, Name = "Cardiology", TenantId = TenantA, IsActive = true },
            new Department { Id = 20, Name = "Neurology", TenantId = TenantB, IsActive = true });

        if (!noGrant)
        {
            db.PermissionGrants.Add(new PermissionGrant
            {
                TenantId = grantSystemWide ? null : grantTenantId,
                PrincipalType = "external",
                ExternalPrincipalId = "scoped@example.test",
                Permissions = "Schedule.Read,Directory.Read",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        db.SaveChanges();
        return factory;
    }

    /// <summary>
    /// A token with a viewer role (so Directory.Read passes the endpoint policy) whose
    /// email matches the seeded grant — mirroring how a real Google user is recognised.
    /// </summary>
    private static string UserToken(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(42, "scoped@example.test", "Scoped User", new[] { "OnCall.Viewer" });
    }

    private static async Task<List<Department>?> GetDepartments(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/departments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<List<Department>>();
    }

    [Fact]
    public async Task GrantScopedToOneTenant_SeesOnlyThatTenantsDepartments()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        var departments = await GetDepartments(client, UserToken(factory));

        departments!.Select(d => d.Name).Should().BeEquivalentTo("Cardiology");
    }

    [Fact]
    public async Task SystemWideGrant_SeesAllTenants()
    {
        using var factory = Seed(grantTenantId: null, grantSystemWide: true);
        using var client = factory.CreateClient();

        var departments = await GetDepartments(client, UserToken(factory));

        departments!.Select(d => d.Name).Should().BeEquivalentTo("Cardiology", "Neurology");
    }

    [Fact]
    public async Task NoGrantAtAll_IsDeniedOutright()
    {
        // Directory.Read comes from the Permission claim, not from a role, so someone with
        // no grant never reaches the handler. Denial is stronger than an empty list.
        using var factory = Seed(grantTenantId: null, noGrant: true);
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        var token = jwt.GenerateToken(99, "nobody@example.test", "Nobody", new[] { "OnCall.Viewer" });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/departments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GrantForDeactivatedTenant_ReachesHandlerButSeesNothing()
    {
        // The fail-closed filter itself: the grant still carries Directory.Read, so the
        // request is authorized, but the tenant is deactivated so it resolves to no
        // tenants. Previously the filter was skipped in exactly this case and the user
        // would have seen every tenant's departments.
        using var factory = Seed(grantTenantId: TenantA);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tenant = db.Tenants.First(t => t.Id == TenantA);
            tenant.IsActive = false;
            db.SaveChanges();
        }

        using var client = factory.CreateClient();

        var departments = await GetDepartments(client, UserToken(factory));

        departments.Should().BeEmpty();
    }

    [Fact]
    public async Task TenantAdmin_SeesTheirTenantOnly()
    {
        using var factory = Seed(grantTenantId: null, noGrant: true);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TenantAdmins.Add(new TenantAdmin
            {
                TenantId = TenantB,
                AzureAdObjectId = "local-55",
                Role = "DepartmentAdmin",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        using var client = factory.CreateClient();
        using var tokenScope = factory.Services.CreateScope();
        var jwt = tokenScope.ServiceProvider.GetRequiredService<LocalJwtService>();
        var token = jwt.GenerateToken(55, "deptadmin@example.test", "Dept Admin", new[] { "OnCall.Viewer" });

        var departments = await GetDepartments(client, token);

        departments!.Select(d => d.Name).Should().BeEquivalentTo("Neurology");
    }
}
