using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Authentication;
using OnCallApi.Controllers;
using OnCallApi.Data;
using OnCallApi.Models;

namespace BackendTests.Controllers;

/// <summary>
/// Sub-admins (DepartmentAdmin → Admin.Scoped) may provision users in their own tenant.
/// These tests exist for the other half of that: the boundaries that make the delegation
/// safe. A scoped admin must not grant system-wide, reach into a tenant they do not
/// administer, or hand out administrative permissions — otherwise delegating this would
/// let a department admin escalate themselves or expose another tenant's data.
///
/// Real tokens, not the development auth handler: that handler pre-seeds "TenantId:"
/// claims, which makes TenantClaimsMiddleware skip claim expansion, so Admin.Scoped would
/// never be issued and the tests would prove nothing about the real path.
/// </summary>
[Collection(WebHostCollection.Name)]
public class DelegatedGrantTests
{
    private const string SigningKey = "test-signing-key-for-delegated-grants-0123456789";
    private const int MyTenant = 1;
    private const int OtherTenant = 2;

    /// <summary>LocalJwtService issues "local-{userId}" as the object id.</summary>
    private const int ScopedAdminUserId = 7;
    private const string ScopedAdminObjectId = "local-7";

    private static WebApplicationFactory<Program> CreateFactory(bool asScopedAdmin)
    {
        var dbName = $"delegated-grant-{Guid.NewGuid():N}";
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // Must be UseSetting: Program.cs reads DevAuth:Enabled eagerly while composing
            // the builder, before ConfigureAppConfiguration callbacks apply. Left on, every
            // request would be auto-authenticated as a full admin.
            builder.UseSetting("DevAuth:Enabled", "false");
            builder.UseSetting("Authentication:Local:SigningKey", SigningKey);

            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.AddRange(
                new Tenant { Id = MyTenant, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
                new Tenant { Id = OtherTenant, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });

            if (asScopedAdmin)
            {
                // This row is what TenantClaimsMiddleware turns into Admin.Scoped.
                db.TenantAdmins.Add(new TenantAdmin
                {
                    TenantId = MyTenant,
                    AzureAdObjectId = ScopedAdminObjectId,
                    Role = "DepartmentAdmin",
                    CreatedAt = DateTime.UtcNow,
                });
            }

            db.SaveChanges();
        }

        return factory;
    }

    /// <summary>A token carrying no admin roles — any admin rights must come from the DB.</summary>
    private static string ScopedAdminToken(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(ScopedAdminUserId, "deptadmin@example.test", "Dept Admin", new[] { "OnCall.Viewer" });
    }

    private static HttpRequestMessage Post(string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/permissions")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task ScopedAdmin_CanGrantWithinOwnTenant()
    {
        using var factory = CreateFactory(asScopedAdmin: true);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(ScopedAdminToken(factory), new
        {
            tenantId = MyTenant,
            externalPrincipalId = "newuser@example.test",
            permissions = "Schedule.Read,Directory.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var grant = await response.Content.ReadFromJsonAsync<PermissionGrantResponse>();
        grant!.TenantId.Should().Be(MyTenant);
        grant.Permissions.Should().BeEquivalentTo("Schedule.Read", "Directory.Read");
    }

    [Fact]
    public async Task ScopedAdmin_CannotGrantSystemWide()
    {
        using var factory = CreateFactory(asScopedAdmin: true);
        using var client = factory.CreateClient();

        // Omitting tenantId means "all tenants" — a super-admin-only reach.
        using var response = await client.SendAsync(Post(ScopedAdminToken(factory), new
        {
            externalPrincipalId = "newuser@example.test",
            permissions = "Schedule.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ScopedAdmin_CannotGrantIntoAnotherTenant()
    {
        using var factory = CreateFactory(asScopedAdmin: true);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(ScopedAdminToken(factory), new
        {
            tenantId = OtherTenant,
            externalPrincipalId = "newuser@example.test",
            permissions = "Schedule.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Admin.Full")]
    [InlineData("Tenant.Manage")]
    [InlineData("Admin.Scoped")]
    [InlineData("Schedule.Read,Admin.Full")]
    public async Task ScopedAdmin_CannotGrantAdministrativePermissions(string permissions)
    {
        using var factory = CreateFactory(asScopedAdmin: true);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(ScopedAdminToken(factory), new
        {
            tenantId = MyTenant,
            externalPrincipalId = "climber@example.test",
            permissions,
        }));

        if (response.StatusCode == HttpStatusCode.OK)
        {
            // A mixed request may succeed, but the administrative permission must be
            // stripped — otherwise a sub-admin could mint another Admin.Full.
            var grant = await response.Content.ReadFromJsonAsync<PermissionGrantResponse>();
            grant!.Permissions.Should().NotContain(p =>
                p == "Admin.Full" || p == "Tenant.Manage" || p == "Admin.Scoped");
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task UserWithoutAnyAdminRow_CannotGrantAtAll()
    {
        // Same token, but no TenantAdmin row: no Admin.Scoped, so no access.
        using var factory = CreateFactory(asScopedAdmin: false);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(ScopedAdminToken(factory), new
        {
            tenantId = MyTenant,
            externalPrincipalId = "newuser@example.test",
            permissions = "Schedule.Read",
        }));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ScopedAdmin_CannotDeleteAnotherTenantsGrant()
    {
        using var factory = CreateFactory(asScopedAdmin: true);

        int foreignGrantId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var grant = new PermissionGrant
            {
                TenantId = OtherTenant,
                PrincipalType = "external",
                ExternalPrincipalId = "someone@other.test",
                Permissions = "Schedule.Read",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };
            db.PermissionGrants.Add(grant);
            db.SaveChanges();
            foreignGrantId = grant.Id;
        }

        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/permissions/{foreignGrantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScopedAdminToken(factory));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
