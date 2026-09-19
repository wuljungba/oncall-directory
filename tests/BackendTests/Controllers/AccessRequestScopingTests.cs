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
using OnCallApi.Services;

namespace BackendTests.Controllers;

/// <summary>
/// Who may read the queue of people asking for access.
///
/// Submitting is anonymous, so the queue fills with strangers' work addresses, their names and
/// whatever they typed into a free-text note. Reading it was open to any admin including a
/// scoped one — an administrator of a single customer — which handed one hospital every other
/// hospital's prospective staff.
///
/// Scoping it needs an answer to "whose request is this?", and the only trustworthy one is the
/// address's domain checked against a directory OnCall has actually read. These pin that the
/// attribution comes from that evidence and nothing weaker, and that everything it cannot
/// attribute stays with the admins who could see it all anyway.
/// </summary>
[Collection(WebHostCollection.Name)]
public class AccessRequestScopingTests
{
    private const string SigningKey = "test-signing-key-for-access-request-scoping-0123456789";
    private const string SuperAdminEmail = "boss@oncall.test";

    private const int MineTenant = 6001;    // the scoped admin administers this one
    private const int TheirsTenant = 6002;
    private const int ScopedAdminUserId = 11;

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"access-request-scoping-{Guid.NewGuid():N}";
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
                new Tenant
                {
                    Id = MineTenant, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow,
                    AzureAdTenantId = "11111111-1111-1111-1111-111111111111",
                    DirectoryDomains = "[\"main.test\",\"main.onmicrosoft.test\"]",
                    DirectoryVerifiedAt = DateTime.UtcNow,
                },
                new Tenant
                {
                    Id = TheirsTenant, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow,
                    AzureAdTenantId = "22222222-2222-2222-2222-222222222222",
                    DirectoryDomains = "[\"north.test\"]",
                    DirectoryVerifiedAt = DateTime.UtcNow,
                });

            // A local token presents oid = "local-{userId}"; this is what makes this user a
            // scoped admin of MineTenant and nothing else.
            db.TenantAdmins.Add(new TenantAdmin
            {
                TenantId = MineTenant,
                AzureAdObjectId = $"local-{ScopedAdminUserId}",
                Role = "DepartmentAdmin",
                CreatedAt = DateTime.UtcNow,
            });

            db.SaveChanges();
        }

        return factory;
    }

    private static void Seed(WebApplicationFactory<Program> factory, params AccessRequest[] requests)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AccessRequests.AddRange(requests);
        db.SaveChanges();
    }

    private static AccessRequest Request(string email, int? tenantId) => new()
    {
        Email = email,
        FullName = "Someone",
        Note = "Please let me in.",
        TenantId = tenantId,
        Status = AccessRequestStatus.Pending,
        CreatedAt = DateTime.UtcNow,
    };

    private static string Token(WebApplicationFactory<Program> factory, int userId, string email)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(userId, email, "Someone", new[] { "OnCall.Viewer" });
    }

    private static async Task<List<AccessRequest>> List(
        WebApplicationFactory<Program> factory, int userId, string email)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/access-requests?status=all");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(factory, userId, email));

        using var response = await factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<AccessRequest>>())!;
    }

    // ── Who sees what ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AScopedAdminSeesOnlyTheirOwnSubscriptionsRequests()
    {
        using var factory = CreateFactory();
        Seed(factory,
            Request("newcomer@main.test", MineTenant),
            Request("rival@north.test", TheirsTenant));

        var seen = await List(factory, ScopedAdminUserId, "deptadmin@main.test");

        seen.Should().ContainSingle().Which.Email.Should().Be("newcomer@main.test");
    }

    /// <summary>
    /// The common case, and the one that decides the default: most requests match no connected
    /// directory at all. Showing them to every scoped admin would leak exactly what scoping is
    /// for, so "nobody could say whose this is" resolves to the narrower audience.
    /// </summary>
    [Fact]
    public async Task AScopedAdminDoesNotSeeRequestsNobodyCouldAttribute()
    {
        using var factory = CreateFactory();
        Seed(factory, Request("stranger@gmail.test", null));

        (await List(factory, ScopedAdminUserId, "deptadmin@main.test")).Should().BeEmpty();
    }

    [Fact]
    public async Task ASuperAdminStillSeesEverythingIncludingTheUnattributed()
    {
        using var factory = CreateFactory();
        Seed(factory,
            Request("newcomer@main.test", MineTenant),
            Request("rival@north.test", TheirsTenant),
            Request("stranger@gmail.test", null));

        var seen = await List(factory, 99, SuperAdminEmail);

        seen.Should().HaveCount(3);
    }

    // ── Where the attribution comes from ─────────────────────────────────────────────────

    private static async Task<AccessRequest?> SubmitAndRead(
        WebApplicationFactory<Program> factory, string email, string? organization = null)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAccessRequestService>();
        (await service.SubmitAsync(new SubmitAccessRequest(email, "New Person", organization, null, null)))
            .Should().BeTrue();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AccessRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Email == email);
    }

    [Fact]
    public async Task AnAddressInAVerifiedDomainIsAttributedToThatSubscription()
    {
        using var factory = CreateFactory();

        var stored = await SubmitAndRead(factory, "newcomer@main.test");

        stored!.TenantId.Should().Be(MineTenant);
        stored.MatchedDomain.Should().Be("main.test");
    }

    /// <summary>
    /// The organisation someone types is a claim about themselves. If it counted, anyone could
    /// put their own request — name, address, free text — into any customer's queue by naming
    /// that customer.
    /// </summary>
    [Fact]
    public async Task TheOrganisationSomebodyTypesAttributesNothing()
    {
        using var factory = CreateFactory();

        var stored = await SubmitAndRead(factory, "impostor@gmail.test", organization: "Main Hospital");

        stored!.TenantId.Should().BeNull();
    }

    /// <summary>
    /// Domains recorded without a live read are a typed claim — the same thing the consent
    /// callback exists to stop being trusted.
    /// </summary>
    [Fact]
    public async Task DomainsOnADirectoryNobodyHasReadAttributeNothing()
    {
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Single(t => t.Id == MineTenant).DirectoryVerifiedAt = null;
            db.SaveChanges();
        }

        (await SubmitAndRead(factory, "newcomer@main.test"))!.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task ADomainTwoSubscriptionsClaimIsLeftUnattributed()
    {
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Single(t => t.Id == TheirsTenant).DirectoryDomains = "[\"north.test\",\"main.test\"]";
            db.SaveChanges();
        }

        // Guessing would hand a stranger's details to whichever subscription sorted first.
        (await SubmitAndRead(factory, "newcomer@main.test"))!.TenantId.Should().BeNull();
    }

    /// <summary>A directory that connects later answers a question that had no answer before.</summary>
    [Fact]
    public async Task ResubmittingReAttributesOnceTheDirectoryIsConnected()
    {
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Single(t => t.Id == MineTenant).DirectoryVerifiedAt = null;
            db.SaveChanges();
        }

        (await SubmitAndRead(factory, "newcomer@main.test"))!.TenantId.Should().BeNull();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Single(t => t.Id == MineTenant).DirectoryVerifiedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        (await SubmitAndRead(factory, "newcomer@main.test"))!.TenantId.Should().Be(MineTenant);
    }

    [Fact]
    public async Task AMalformedStoredDomainListAttributesNothingRatherThanThrowing()
    {
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Single(t => t.Id == MineTenant).DirectoryDomains = "not json at all";
            db.SaveChanges();
        }

        (await SubmitAndRead(factory, "newcomer@main.test"))!.TenantId.Should().BeNull();
    }
}
