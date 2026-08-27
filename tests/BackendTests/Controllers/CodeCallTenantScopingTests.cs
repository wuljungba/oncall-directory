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
/// The code-call surface must be tenant-scoped, on reads and on writes.
///
/// It was neither. PhoneTreeEventService carried no tenant filter at all, so every
/// customer's live incidents — including Location, LocationZone and Notes — were readable
/// by anyone holding the baseline Directory.Read permission. Worse, starting a code call
/// took the phone tree id straight from the route and dispatched on it, so any ordinary
/// scheduler could page another hospital's on-call clinicians.
///
/// Out-of-tenant ids must read as "not found" rather than "forbidden", so an endpoint
/// never confirms that another customer's incident exists.
/// </summary>
[Collection(WebHostCollection.Name)]
public class CodeCallTenantScopingTests
{
    private const string SigningKey = "test-signing-key-for-code-call-scoping-0123456789";
    private const int TenantA = 1;
    private const int TenantB = 2;
    private const int TreeA = 11;
    private const int TreeB = 22;
    private const int EventA = 111;
    private const int EventB = 222;

    private static WebApplicationFactory<Program> CreateFactory(string dbName)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("DevAuth:Enabled", "false");
            builder.UseSetting("Authentication:Local:SigningKey", SigningKey);

            // appsettings.Development.json enables Twilio. These tests deliberately drive
            // the code-call path, so every dispatch channel is forced off: a test must
            // never be able to page a real phone.
            builder.UseSetting("Dispatch:Twilio:Enabled", "false");
            builder.UseSetting("Dispatch:Cucm:Enabled", "false");
            builder.UseSetting("Dispatch:InformaCast:Enabled", "false");
            builder.UseSetting("Dispatch:Vocera:Enabled", "false");
            builder.UseSetting("Dispatch:SipPbx:Enabled", "false");

            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });
    }

    /// <summary>Two tenants, each with a department, a phone tree and one active incident.</summary>
    private static WebApplicationFactory<Program> Seed(int? grantTenantId, bool grantSystemWide = false)
    {
        var factory = CreateFactory($"code-call-scoping-{Guid.NewGuid():N}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = TenantB, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.Departments.AddRange(
            new Department { Id = 10, Name = "Cardiology", TenantId = TenantA, IsActive = true },
            new Department { Id = 20, Name = "Neurology", TenantId = TenantB, IsActive = true });
        db.PhoneTrees.AddRange(
            new PhoneTree { Id = TreeA, Name = "Code Blue A", TreeType = "code-blue", DepartmentId = 10, IsActive = true },
            new PhoneTree { Id = TreeB, Name = "Code Blue B", TreeType = "code-blue", DepartmentId = 20, IsActive = true });

        var now = DateTime.UtcNow;
        db.PhoneTreeEvents.AddRange(
            new PhoneTreeEvent
            {
                Id = EventA, PhoneTreeId = TreeA, Status = "active", StartedAt = now,
                Location = "A-Ward-3", LocationZone = "A-Zone", Notes = "tenant A incident",
            },
            new PhoneTreeEvent
            {
                Id = EventB, PhoneTreeId = TreeB, Status = "active", StartedAt = now,
                Location = "B-Ward-9", LocationZone = "B-Zone", Notes = "tenant B incident",
            });

        db.PermissionGrants.Add(new PermissionGrant
        {
            TenantId = grantSystemWide ? null : grantTenantId,
            PrincipalType = "external",
            ExternalPrincipalId = "scoped@example.test",
            // Schedule.Write is the ordinary scheduler permission that gates starting a
            // code call — the point being that it must not reach across tenants.
            Permissions = "Schedule.Read,Schedule.Write,Directory.Read",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });

        db.SaveChanges();
        return factory;
    }

    private static string UserToken(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(42, "scoped@example.test", "Scoped User", new[] { "OnCall.Viewer", "OnCall.Scheduler" });
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, string token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static (int Events, int Steps) Counts(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (db.PhoneTreeEvents.Count(), db.DispatchSteps.Count());
    }

    // ── Starting a code call ──

    /// <summary>
    /// The critical one: a scheduler in tenant A must not be able to page tenant B, and the
    /// refusal must leave no trace behind — no incident row, no dispatch step.
    /// </summary>
    [Fact]
    public async Task StartingACodeCallInAnotherTenantsTreeIsRejectedAndDispatchesNothing()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();
        var before = Counts(factory);

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, $"/api/phone-trees/{TreeB}/events",
            new { Confirm = true, Location = "attacker-supplied", RequestedByName = "Mallory" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Counts(factory).Should().Be(before, "a refused code call must create no incident and no dispatch step");
    }

    [Fact]
    public async Task StartingACodeCallInTheCallersOwnTreeSucceeds()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, $"/api/phone-trees/{TreeA}/events",
            new { Confirm = true, Location = "A-Ward-4", RequestedByName = "Alice" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        Counts(factory).Events.Should().Be(3);
    }

    /// <summary>Consent is still required, and it is a separate gate from authorization.</summary>
    [Fact]
    public async Task StartingACodeCallWithoutOperatorConfirmationIsRejected()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();
        var before = Counts(factory);

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, $"/api/phone-trees/{TreeA}/events",
            new { Confirm = false });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Counts(factory).Should().Be(before);
    }

    // ── Reading incidents ──

    [Fact]
    public async Task ActiveIncidentsAreScopedToTheCallersTenant()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(client, UserToken(factory), HttpMethod.Get, "/api/phone-trees/events/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<List<PhoneTreeEvent>>();
        events!.Select(e => e.Id).Should().BeEquivalentTo(new[] { EventA });
    }

    [Fact]
    public async Task ReadingAnotherTenantsIncidentByIdIsNotFound()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(client, UserToken(factory), HttpMethod.Get, $"/api/phone-trees/events/{EventB}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListingIncidentsForAnotherTenantsTreeReturnsNothing()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(client, UserToken(factory), HttpMethod.Get, $"/api/phone-trees/{TreeB}/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<List<PhoneTreeEvent>>();
        events!.Should().BeEmpty();
    }

    // ── Acting on incidents ──

    [Fact]
    public async Task AcknowledgingAnotherTenantsIncidentIsNotFound()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, $"/api/phone-trees/events/{EventB}/acknowledge");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PhoneTreeEvents.Single(e => e.Id == EventB).AcknowledgedAt
            .Should().BeNull("the other tenant's incident must be untouched");
    }

    [Fact]
    public async Task ResolvingAnotherTenantsIncidentIsNotFound()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, $"/api/phone-trees/events/{EventB}/resolve",
            new { Outcome = "hijacked" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var untouched = db.PhoneTreeEvents.Single(e => e.Id == EventB);
        untouched.Status.Should().Be("active");
        untouched.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task AcknowledgingTheCallersOwnIncidentSucceeds()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, $"/api/phone-trees/events/{EventA}/acknowledge");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Whole-estate access ──

    [Fact]
    public async Task SystemWideGrantSeesEveryTenantsIncidents()
    {
        using var factory = Seed(grantTenantId: null, grantSystemWide: true);
        using var client = factory.CreateClient();

        using var response = await Send(client, UserToken(factory), HttpMethod.Get, "/api/phone-trees/events/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<List<PhoneTreeEvent>>();
        events!.Select(e => e.Id).Should().BeEquivalentTo(new[] { EventA, EventB });
    }

    // ── Phone tree writes ──
    //
    // Reads were scoped and writes were not, so the escalation tree a code call follows
    // could be rewritten from another tenant. That is the difference between leaking data
    // and silently disabling a hospital's code-blue paging.

    [Fact]
    public async Task RewritingAnotherTenantsPhoneTreeIsNotFoundAndLeavesItUnchanged()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Put, $"/api/phone-trees/{TreeB}",
            new PhoneTree { Id = TreeB, Name = "Hijacked", TreeType = "code-blue", DepartmentId = 20, IsActive = false });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var untouched = db.PhoneTrees.Single(t => t.Id == TreeB);
        untouched.Name.Should().Be("Code Blue B");
        untouched.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task AddingANodeToAnotherTenantsPhoneTreeIsNotFound()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, $"/api/phone-trees/{TreeB}/nodes",
            new PhoneTreeNode { PhoneTreeId = TreeB, Order = 1, RoleName = "Injected" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PhoneTreeNodes.Count(n => n.PhoneTreeId == TreeB).Should().Be(0);
    }

    [Fact]
    public async Task ReorderingAnotherTenantsPhoneTreeIsNotFound()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, $"/api/phone-trees/{TreeB}/reorder", new List<int> { 1, 2 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreatingAPhoneTreeInAnotherTenantsDepartmentIsRejected()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Post, "/api/phone-trees",
            new PhoneTree { Name = "Planted", TreeType = "code-blue", DepartmentId = 20, IsActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PhoneTrees.Any(t => t.Name == "Planted").Should().BeFalse();
    }

    [Fact]
    public async Task UpdatingTheCallersOwnPhoneTreeSucceeds()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await Send(
            client, UserToken(factory), HttpMethod.Put, $"/api/phone-trees/{TreeA}",
            new PhoneTree { Id = TreeA, Name = "Code Blue A (revised)", TreeType = "code-blue", DepartmentId = 10, IsActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PhoneTrees.Single(t => t.Id == TreeA).Name.Should().Be("Code Blue A (revised)");
    }
}
