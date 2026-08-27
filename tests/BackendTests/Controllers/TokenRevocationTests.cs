using System.Net;
using System.Net.Http.Headers;
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
/// Deactivating an account must end its sessions.
///
/// Local tokens are self-contained and were checked against the database only at sign-in,
/// so IsActive=false did nothing to a token already issued: a departing employee — or
/// someone whose access was pulled precisely because it was being misused — kept working
/// access until the token expired on its own, up to 24 hours later. There is no token
/// blocklist and no refresh rotation, so the account has to be re-checked per request.
/// </summary>
[Collection(WebHostCollection.Name)]
public class TokenRevocationTests
{
    private const string SigningKey = "test-signing-key-for-token-revocation-0123456789";
    private const int AccountId = 4242;

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"revocation-{Guid.NewGuid():N}";
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

    /// <summary>An active local account with a system-wide grant, so it can reach a real endpoint.</summary>
    private static WebApplicationFactory<Program> Seed()
    {
        var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Tenants.Add(new Tenant { Id = 1, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.Departments.Add(new Department { Id = 10, Name = "Cardiology", TenantId = 1, IsActive = true });
        db.LocalAccounts.Add(new LocalAccount
        {
            Id = AccountId,
            Email = "staffer@example.test",
            DisplayName = "Staffer",
            PasswordHash = "not-used-in-this-test",
            Roles = ["OnCall.Viewer"],
            IsActive = true,
        });
        db.PermissionGrants.Add(new PermissionGrant
        {
            TenantId = 1,
            PrincipalType = "external",
            ExternalPrincipalId = "staffer@example.test",
            Permissions = "Schedule.Read,Directory.Read",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return factory;
    }

    private static string TokenFor(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(AccountId, "staffer@example.test", "Staffer", new[] { "OnCall.Viewer" });
    }

    private static void SetAccountActive(WebApplicationFactory<Program> factory, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LocalAccounts.Single(a => a.Id == AccountId).IsActive = isActive;
        db.SaveChanges();
    }

    private static async Task<HttpStatusCode> GetDepartments(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/departments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task AnActiveAccountsTokenIsAccepted()
    {
        using var factory = Seed();
        using var client = factory.CreateClient();

        (await GetDepartments(client, TokenFor(factory))).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The same token, unchanged and unexpired, before and after deactivation.
    /// </summary>
    [Fact]
    public async Task DeactivatingAnAccountRejectsItsExistingToken()
    {
        using var factory = Seed();
        using var client = factory.CreateClient();
        var token = TokenFor(factory);

        (await GetDepartments(client, token)).Should().Be(HttpStatusCode.OK);

        SetAccountActive(factory, false);

        (await GetDepartments(client, token)).Should().Be(HttpStatusCode.Unauthorized,
            "a deactivated account must not keep working access until its token expires");
    }

    [Fact]
    public async Task ReactivatingAnAccountRestoresAccessOnTheNextRequest()
    {
        using var factory = Seed();
        using var client = factory.CreateClient();
        var token = TokenFor(factory);

        SetAccountActive(factory, false);
        (await GetDepartments(client, token)).Should().Be(HttpStatusCode.Unauthorized);

        SetAccountActive(factory, true);
        (await GetDepartments(client, token)).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Pins the half that already worked, so an optimisation that starts caching claims
    /// cannot quietly break it: a revoked grant takes effect immediately, because
    /// TenantClaimsMiddleware re-reads it per request.
    /// </summary>
    [Fact]
    public async Task RevokingAPermissionGrantTakesEffectOnTheNextRequest()
    {
        using var factory = Seed();
        using var client = factory.CreateClient();
        var token = TokenFor(factory);

        (await GetDepartments(client, token)).Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PermissionGrants.Single(g => g.ExternalPrincipalId == "staffer@example.test").IsActive = false;
            db.SaveChanges();
        }

        (await GetDepartments(client, token)).Should().Be(HttpStatusCode.Forbidden);
    }
}
