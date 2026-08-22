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
/// HIPAA §164.312(b) requires PHI access to be attributable to a unique user, and a
/// denied attempt is precisely the event an audit trail exists to capture.
///
/// Both were broken. The middleware read only the Entra-specific object-id claim, so every
/// Google and local user was recorded as Guid.Empty — attributable to nobody. And it ran
/// AFTER UseAuthorization, which short-circuits on 401/403, so refused attempts to reach
/// PHI were never written at all.
/// </summary>
[Collection(WebHostCollection.Name)]
public class HipaaAuditTests
{
    private const string SigningKey = "test-signing-key-for-hipaa-audit-tests-0123456789";

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

    private static string LocalToken(WebApplicationFactory<Program> factory, int userId, string email)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(userId, email, "Audit Test", new[] { "OnCall.Viewer" });
    }

    /// <summary>Audit writes go through a background flusher, so poll for them.</summary>
    private static async Task<List<AuditLog>> WaitForAuditLogs(
        WebApplicationFactory<Program> factory, Func<AuditLog, bool> predicate, int expected = 1)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var matches = (await db.AuditLogs.AsNoTracking().ToListAsync()).Where(predicate).ToList();
            if (matches.Count >= expected) return matches;
            await Task.Delay(250);
        }
        return [];
    }

    [Fact]
    public async Task DeniedPhiAccess_IsAudited()
    {
        using var factory = CreateFactory($"audit-denied-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        // A viewer token carries no Directory.Read permission claim, so this is refused.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/directory/search?q=smith");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalToken(factory, 501, "denied@example.test"));
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var logs = await WaitForAuditLogs(factory, l => l.ResourceType == "directory");
        logs.Should().NotBeEmpty("a refused attempt to read PHI must be recorded");
        logs[0].StatusCode.Should().Be(403);
        logs[0].PrincipalId.Should().Be("local-501");
    }

    [Fact]
    public async Task NonEntraUser_IsAttributedNotAnonymous()
    {
        using var factory = CreateFactory($"audit-principal-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/schedule");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalToken(factory, 777, "local-user@example.test"));
        using var _ = await client.SendAsync(request);

        var logs = await WaitForAuditLogs(factory, l => l.PrincipalId == "local-777");

        logs.Should().NotBeEmpty();
        // UserId stays Guid.Empty because a local id is not a GUID — which is exactly why
        // PrincipalId had to exist for the record to identify anyone at all.
        logs[0].UserId.Should().Be(Guid.Empty);
        logs[0].PrincipalId.Should().Be("local-777");
        logs[0].UserName.Should().NotBe("unknown");
    }

    [Fact]
    public async Task AdminEndpoints_AreNowAudited()
    {
        // /api/admin is the employee store and was outside the audited prefixes entirely.
        using var factory = CreateFactory($"audit-admin-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/employees");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalToken(factory, 888, "admin-probe@example.test"));
        using var _ = await client.SendAsync(request);

        var logs = await WaitForAuditLogs(factory, l => l.ResourceType == "admin");

        logs.Should().NotBeEmpty("the employee store must be audited");
    }

    [Fact]
    public async Task AnonymousAttemptOnPhiRoute_IsAudited()
    {
        using var factory = CreateFactory($"audit-anon-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/directory/search?q=smith");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var logs = await WaitForAuditLogs(factory, l => l.ResourceType == "directory");
        logs.Should().NotBeEmpty("an unauthenticated probe at PHI is worth recording");
        logs[0].StatusCode.Should().Be(401);
        logs[0].UserName.Should().Be("anonymous");
    }

    [Fact]
    public async Task NonPhiRoutes_AreNotAudited()
    {
        using var factory = CreateFactory($"audit-health-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        using var _ = await client.GetAsync("/health");
        await Task.Delay(1500);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }
}
