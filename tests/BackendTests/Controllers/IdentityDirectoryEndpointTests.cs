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
/// End to end: signing in must make a person visible to an administrator, flagged as
/// having no access, so they can be granted permissions from a list instead of the admin
/// needing to already know their email address.
/// </summary>
[Collection(WebHostCollection.Name)]
public class IdentityDirectoryEndpointTests
{
    private const string SigningKey = "test-signing-key-for-identity-directory-0123456789";
    private const string SuperAdminEmail = "boss@example.test";

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"identity-directory-{Guid.NewGuid():N}";
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
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

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Add(new Tenant { Id = 1, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        return factory;
    }

    private static string Token(WebApplicationFactory<Program> factory, int userId, string email, string name)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(userId, email, name, new[] { "OnCall.Viewer" });
    }

    private static async Task CallAs(HttpClient client, string token, string path = "/api/auth/me")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var _ = await client.SendAsync(request);
    }

    /// <summary>
    /// Recording happens on a background flusher, so poll rather than assuming timing.
    /// </summary>
    private static async Task<List<SignInIdentityResponse>> WaitForIdentities(
        WebApplicationFactory<Program> factory, HttpClient client, string adminToken, int expected)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/identities");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var list = await response.Content.ReadFromJsonAsync<List<SignInIdentityResponse>>() ?? [];
            if (list.Count >= expected) return list;

            await Task.Delay(250);
        }

        return [];
    }

    [Fact]
    public async Task SignedInUser_BecomesVisible_AndIsFlaggedAsHavingNoAccess()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var newcomer = Token(factory, 101, "newcomer@example.test", "New Comer");
        var admin = Token(factory, 1, SuperAdminEmail, "The Boss");

        // The newcomer simply uses the app; nothing else provisions them.
        await CallAs(client, newcomer);
        await CallAs(client, admin);

        var identities = await WaitForIdentities(factory, client, admin, expected: 2);

        var recorded = identities.FirstOrDefault(i => i.Email == "newcomer@example.test");
        recorded.Should().NotBeNull("signing in must make a user discoverable");
        recorded!.DisplayName.Should().Be("New Comer");
        recorded.Provider.Should().Be("local");
        recorded.HasNoAccess.Should().BeTrue();
        recorded.Permissions.Should().BeEmpty();
        // This is the value needed to appoint a sub-admin, and is otherwise undiscoverable.
        recorded.ExternalObjectId.Should().Be("local-101");
    }

    [Fact]
    public async Task ConfiguredSuperAdmin_IsLabelledAsSuchRatherThanNoAccess()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var admin = Token(factory, 1, SuperAdminEmail, "The Boss");
        await CallAs(client, admin);

        var identities = await WaitForIdentities(factory, client, admin, expected: 1);

        var boss = identities.Single(i => i.Email == SuperAdminEmail);
        boss.IsSuperAdmin.Should().BeTrue();
        boss.HasNoAccess.Should().BeFalse();
    }

    [Fact]
    public async Task GrantedUser_ShowsTheirPermissions()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PermissionGrants.Add(new PermissionGrant
            {
                TenantId = 1,
                PrincipalType = "external",
                ExternalPrincipalId = "granted@example.test",
                Permissions = "Schedule.Read,Directory.Read",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var granted = Token(factory, 202, "granted@example.test", "Granted User");
        var admin = Token(factory, 1, SuperAdminEmail, "The Boss");
        await CallAs(client, granted);
        await CallAs(client, admin);

        var identities = await WaitForIdentities(factory, client, admin, expected: 2);

        var row = identities.Single(i => i.Email == "granted@example.test");
        row.HasNoAccess.Should().BeFalse();
        row.Permissions.Should().BeEquivalentTo("Directory.Read", "Schedule.Read");
    }

    [Fact]
    public async Task NonAdmin_CannotListIdentities()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var plainUser = Token(factory, 303, "plain@example.test", "Plain User");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/identities");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainUser);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_CannotListIdentities()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/admin/identities");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
