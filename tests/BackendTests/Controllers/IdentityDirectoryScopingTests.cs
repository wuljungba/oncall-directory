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
/// Who a scoped administrator may see in the sign-in directory.
///
/// The pool of unprovisioned sign-ins is global, and the filter used to skip somebody only
/// when they already HELD access elsewhere — so anyone with no access at all was shown to
/// every scoped admin on the deployment, name, address and object id. With more than one
/// customer that discloses one hospital's prospective users to another's administrator.
///
/// It also had no idea whose person somebody was: a Google or local token carries no tenant,
/// so a plain directory contact of one subscription looked like an unattached stranger.
/// Employee records are that missing association.
/// </summary>
[Collection(WebHostCollection.Name)]
public class IdentityDirectoryScopingTests
{
    private const string SigningKey = "test-signing-key-for-identity-scoping-0123456789";
    private const string SuperAdminEmail = "boss@example.test";

    private const int MineTenant = 1;      // the scoped admin administers this one
    private const int TheirsTenant = 2;
    private const int InactiveTenant = 3;  // mirrors a deactivated subscription

    private const string ScopedAdminEmail = "deptadmin@main.test";
    private const string MineEmail = "mine@main.test";
    private const string TheirsEmail = "theirs@north.test";
    private const string InactiveEmail = "contact@deactivated.test";
    private const string StrangerEmail = "stranger@nowhere.test";

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"identity-scoping-{Guid.NewGuid():N}";
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
            db.Tenants.AddRange(
                new Tenant { Id = MineTenant, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
                new Tenant { Id = TheirsTenant, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow },
                new Tenant { Id = InactiveTenant, Name = "Deactivated Site", IsActive = false, CreatedAt = DateTime.UtcNow });

            // A local token presents oid = "local-{userId}"; this is what makes user 7 a
            // scoped admin of MineTenant and nothing else.
            db.TenantAdmins.Add(new TenantAdmin
            {
                TenantId = MineTenant,
                AzureAdObjectId = "local-7",
                Role = "DepartmentAdmin",
                CreatedAt = DateTime.UtcNow,
            });

            // Directory contacts. AzureAdObjectId is left blank on purpose: these people are
            // known only by address, which is the Google and local case.
            db.Employees.AddRange(
                Contact("Mine", "Person", MineEmail, MineTenant),
                Contact("Theirs", "Person", TheirsEmail, TheirsTenant),
                Contact("Inactive", "Contact", InactiveEmail, InactiveTenant),
                // No address at all. Must attribute nobody: string.Equals(null, null) is true,
                // and matching on it would tie every address-less record to every principal.
                Contact("NoAddress", "Contact", null, MineTenant));

            db.SaveChanges();
        }

        return factory;
    }

    private static Employee Contact(string first, string last, string? email, int tenantId) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = first,
        LastName = last,
        Email = email,
        TenantId = tenantId,
        IsActive = true,
    };

    private static string Token(WebApplicationFactory<Program> factory, int userId, string email, string name)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(userId, email, name, new[] { "OnCall.Viewer" });
    }

    private static async Task SignIn(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var _ = await client.SendAsync(request);
    }

    private static async Task<List<SignInIdentityResponse>> ListAs(
        HttpClient client, string token, int expectedAtLeast = 0)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/identities");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var list = await response.Content.ReadFromJsonAsync<List<SignInIdentityResponse>>() ?? [];
            if (list.Count >= expectedAtLeast) return list;
            await Task.Delay(250);
        }

        return [];
    }

    /// <summary>Signs everyone in and returns the super-admin token.</summary>
    private static async Task<string> SeedSignIns(WebApplicationFactory<Program> factory, HttpClient client)
    {
        var boss = Token(factory, 1, SuperAdminEmail, "The Boss");
        await SignIn(client, Token(factory, 7, ScopedAdminEmail, "Dept Admin"));
        await SignIn(client, Token(factory, 11, MineEmail, "Mine Person"));
        await SignIn(client, Token(factory, 12, TheirsEmail, "Theirs Person"));
        await SignIn(client, Token(factory, 13, InactiveEmail, "Inactive Contact"));
        await SignIn(client, Token(factory, 14, StrangerEmail, "A Stranger"));
        await SignIn(client, boss);

        // The recorder is a background flusher, so wait for all six to land.
        await ListAs(client, boss, expectedAtLeast: 6);
        return boss;
    }

    [Fact]
    public async Task ScopedAdmin_SeesOnlyPeopleAttributableToTheirOwnSubscription()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SeedSignIns(factory, client);

        var visible = await ListAs(client, Token(factory, 7, ScopedAdminEmail, "Dept Admin"));
        var emails = visible.Select(i => i.Email).ToList();

        // Their own directory contact, and themselves via the admin appointment.
        emails.Should().Contain(MineEmail);
        emails.Should().Contain(ScopedAdminEmail);

        // Another subscription's contact — the reported bug.
        emails.Should().NotContain(TheirsEmail);

        // A deactivated subscription's contact stays attributed to it. Were attribution
        // filtered on the tenant being active, deactivating a subscription would push its
        // people back into every other administrator's list.
        emails.Should().NotContain(InactiveEmail);

        // Attributable to nobody: a genuine unknown, left to super admins.
        emails.Should().NotContain(StrangerEmail);

        // And the super admin is not somebody a sub-admin needs to see.
        emails.Should().NotContain(SuperAdminEmail);
    }

    [Fact]
    public async Task SuperAdmin_StillSeesEveryoneIncludingUnattachedNewcomers()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var boss = await SeedSignIns(factory, client);

        var emails = (await ListAs(client, boss, expectedAtLeast: 6)).Select(i => i.Email).ToList();

        emails.Should().Contain(new[]
        {
            ScopedAdminEmail, MineEmail, TheirsEmail, InactiveEmail, StrangerEmail, SuperAdminEmail,
        });
    }

    [Fact]
    public async Task TheAttributionIsReported_SoTheUiCanSayWhosePersonThisIs()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var boss = await SeedSignIns(factory, client);

        var all = await ListAs(client, boss, expectedAtLeast: 6);

        all.Single(i => i.Email == MineEmail).HomeTenantIds.Should().BeEquivalentTo(new[] { MineTenant });
        all.Single(i => i.Email == InactiveEmail).HomeTenantIds.Should().BeEquivalentTo(new[] { InactiveTenant });
        all.Single(i => i.Email == StrangerEmail).HomeTenantIds.Should().BeEmpty();
    }
}
