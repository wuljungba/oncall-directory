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
/// Granting the same permissions to many people at once.
///
/// The delegation boundaries matter more here than on the single-record path, because bulk
/// multiplies whatever gets through: a scoped admin who could smuggle Admin.Full past the
/// parser would be minting it for a whole subscription in one call, not for one person.
///
/// Real tokens, and DevAuth off, for the same reason as DelegatedGrantTests: the development
/// handler pre-seeds "TenantId:" claims, TenantClaimsMiddleware then skips claim expansion,
/// Admin.Scoped is never issued from the TenantAdmin row, and the guards under test are never
/// reached.
/// </summary>
[Collection(WebHostCollection.Name)]
public class BulkGrantTests
{
    private const string SigningKey = "test-signing-key-for-bulk-grants-0123456789";
    private const int MyTenant = 1;
    private const int OtherTenant = 2;

    private const int ScopedAdminUserId = 7;
    private const string ScopedAdminObjectId = "local-7";

    private static readonly Guid AliceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid BobId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid CarolId = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid UnitId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid NoEmailId = Guid.Parse("10000000-0000-0000-0000-000000000005");

    private static WebApplicationFactory<Program> CreateFactory(string dbName)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.AddRange(
                new Tenant { Id = MyTenant, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
                new Tenant { Id = OtherTenant, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });

            db.TenantAdmins.Add(new TenantAdmin
            {
                TenantId = MyTenant,
                AzureAdObjectId = ScopedAdminObjectId,
                Role = "DepartmentAdmin",
                CreatedAt = DateTime.UtcNow,
            });

            db.Employees.AddRange(
                new Employee { Id = AliceId, FirstName = "Alice", LastName = "Adams", Email = "alice@main.test", TenantId = MyTenant, IsActive = true, AzureAdObjectId = "oid-alice" },
                new Employee { Id = BobId, FirstName = "Bob", LastName = "Baker", Email = "bob@main.test", TenantId = MyTenant, IsActive = true, AzureAdObjectId = "oid-bob" },
                new Employee { Id = CarolId, FirstName = "Carol", LastName = "Clark", Email = "carol@north.test", TenantId = OtherTenant, IsActive = true, AzureAdObjectId = "oid-carol" },
                new Employee { Id = UnitId, FirstName = "", LastName = "", DisplayName = "3North", Email = null, ContactType = "Department", TenantId = MyTenant, IsActive = true, AzureAdObjectId = "csv-import-unit" },
                new Employee { Id = NoEmailId, FirstName = "Erin", LastName = "Evans", Email = null, TenantId = MyTenant, IsActive = true, AzureAdObjectId = "csv-import-erin" });

            db.SaveChanges();
        }

        return factory;
    }

    private static string Token(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(ScopedAdminUserId, "deptadmin@example.test", "Dept Admin", new[] { "OnCall.Viewer" });
    }

    private static HttpRequestMessage Post(string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/permissions/bulk")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static AppDbContext Db(WebApplicationFactory<Program> factory) =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    // ── Delegation boundaries ──

    [Fact]
    public async Task ScopedAdmin_CanBulkGrantWithinOwnTenant()
    {
        using var factory = CreateFactory($"bulk-grant-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = MyTenant,
            employeeIds = new[] { AliceId, BobId },
            permissions = "Schedule.Read,Directory.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = Db(factory);
        var grants = db.PermissionGrants.Where(g => g.IsActive).ToList();
        grants.Should().HaveCount(2);
        grants.Should().OnlyContain(g => g.TenantId == MyTenant);
        grants.Select(g => g.ExternalPrincipalId).Should().BeEquivalentTo(["alice@main.test", "bob@main.test"]);
    }

    [Fact]
    public async Task ScopedAdmin_CannotBulkGrantSystemWide()
    {
        using var factory = CreateFactory($"bulk-grant-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        // Omitting the tenant is the widest grant there is — it resolves to every subscription.
        using var response = await client.SendAsync(Post(Token(factory), new
        {
            employeeIds = new[] { AliceId },
            permissions = "Schedule.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Db(factory).PermissionGrants.Should().BeEmpty();
    }

    [Fact]
    public async Task ScopedAdmin_CannotBulkGrantIntoAnotherTenant()
    {
        using var factory = CreateFactory($"bulk-grant-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = OtherTenant,
            employeeIds = new[] { CarolId },
            permissions = "Schedule.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Db(factory).PermissionGrants.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Admin.Full")]
    [InlineData("Tenant.Manage")]
    [InlineData("Admin.Scoped")]
    [InlineData("Schedule.Read,Admin.Full")]
    public async Task ScopedAdmin_CannotBulkGrantAdministrativePermissions(string permissions)
    {
        using var factory = CreateFactory($"bulk-grant-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = MyTenant,
            employeeIds = new[] { AliceId, BobId },
            permissions,
        }));

        // Either refused outright, or accepted with the administrative permissions stripped —
        // never handed out. Bulk is where a hole here would be multiplied.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var db = Db(factory);
            db.PermissionGrants.ToList()
                .Should().OnlyContain(g => !g.Permissions.Contains("Admin.") && !g.Permissions.Contains("Tenant.Manage"));
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    // ── Replace semantics ──

    [Fact]
    public async Task BulkGrant_ReplacesExistingPermissionsRatherThanAddingASecondRow()
    {
        var dbName = $"bulk-grant-{Guid.NewGuid():N}";
        using var factory = CreateFactory(dbName);
        using (var seed = Db(factory))
        {
            seed.PermissionGrants.Add(new PermissionGrant
            {
                TenantId = MyTenant, PrincipalType = "external", ExternalPrincipalId = "alice@main.test",
                Permissions = "Schedule.Read", IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = MyTenant,
            employeeIds = new[] { AliceId },
            permissions = "Directory.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = Db(factory);
        var grants = db.PermissionGrants.Where(g => g.IsActive).ToList();
        grants.Should().ContainSingle();
        // Replaced, not unioned — otherwise there is no way to correct an over-granted batch.
        grants[0].Permissions.Should().Be("Directory.Read");
    }

    [Fact]
    public async Task BulkGrant_RunTwice_ChangesNothingTheSecondTime()
    {
        using var factory = CreateFactory($"bulk-grant-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();
        var token = Token(factory);
        object body = new { tenantId = MyTenant, employeeIds = new[] { AliceId, BobId }, permissions = "Schedule.Read" };

        using (var first = await client.SendAsync(Post(token, body))) first.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var second = await client.SendAsync(Post(token, body))) second.StatusCode.Should().Be(HttpStatusCode.OK);

        // There is no unique index to stop duplicates piling up, so idempotency has to come from
        // the upsert itself.
        Db(factory).PermissionGrants.Count().Should().Be(2);
    }

    [Fact]
    public async Task BulkGrant_CollapsesDuplicateRowsLeftByTheSingleRecordPath()
    {
        var dbName = $"bulk-grant-{Guid.NewGuid():N}";
        using var factory = CreateFactory(dbName);
        using (var seed = Db(factory))
        {
            // The single-record endpoint always inserts, so these already exist in the wild.
            for (var i = 0; i < 2; i++)
            {
                seed.PermissionGrants.Add(new PermissionGrant
                {
                    TenantId = MyTenant, PrincipalType = "external", ExternalPrincipalId = "alice@main.test",
                    Permissions = "Schedule.Read", IsActive = true, CreatedAt = DateTime.UtcNow,
                });
            }
            seed.SaveChanges();
        }

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = MyTenant, employeeIds = new[] { AliceId }, permissions = "Directory.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = Db(factory);
        db.PermissionGrants.Count(g => g.IsActive).Should().Be(1);
        db.PermissionGrants.Count(g => !g.IsActive).Should().Be(1);
    }

    [Fact]
    public async Task BulkGrant_ReusesAPreviouslyRevokedRowInsteadOfAddingAnother()
    {
        var dbName = $"bulk-grant-{Guid.NewGuid():N}";
        using var factory = CreateFactory(dbName);
        using (var seed = Db(factory))
        {
            seed.PermissionGrants.Add(new PermissionGrant
            {
                TenantId = MyTenant, PrincipalType = "local", ExternalPrincipalId = "alice@main.test",
                Permissions = "Schedule.Write", IsActive = false, CreatedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = MyTenant, employeeIds = new[] { AliceId }, permissions = "Directory.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = Db(factory);
        // Matched despite the differing PrincipalType — the middleware ignores it, so a stale row
        // left beside a new one would keep conferring what this call was meant to replace.
        var grant = db.PermissionGrants.Should().ContainSingle().Subject;
        grant.IsActive.Should().BeTrue();
        grant.Permissions.Should().Be("Directory.Read");
    }

    // ── Who cannot be granted ──

    [Fact]
    public async Task BulkGrant_SkipsPeopleWithNoEmailAndUnitLines()
    {
        using var factory = CreateFactory($"bulk-grant-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = MyTenant,
            employeeIds = new[] { AliceId, NoEmailId, UnitId },
            permissions = "Schedule.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BulkGrantResponse>();

        body!.Skipped.Should().Be(2);
        body.Results.Single(r => r.EmployeeId == NoEmailId).Outcome.Should().Be(BulkOutcomes.SkippedNoEmail);
        body.Results.Single(r => r.EmployeeId == UnitId).Outcome.Should().Be(BulkOutcomes.SkippedNotAPerson);

        // Only Alice — a grant keyed to a synthetic importer id could never match a token.
        Db(factory).PermissionGrants.Should().ContainSingle(g => g.ExternalPrincipalId == "alice@main.test");
    }

    [Fact]
    public async Task BulkGrant_SkipsSomeoneFromAnotherTenantWithoutFailingTheRest()
    {
        using var factory = CreateFactory($"bulk-grant-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = MyTenant,
            employeeIds = new[] { AliceId, CarolId },
            permissions = "Schedule.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BulkGrantResponse>();

        var carol = body!.Results.Single(r => r.EmployeeId == CarolId);
        carol.Outcome.Should().Be(BulkOutcomes.NotFound);
        carol.Email.Should().BeNull("another subscription's record must not be discoverable by id");

        body.Results.Single(r => r.EmployeeId == AliceId).Outcome.Should().Be(BulkOutcomes.Succeeded);
    }

    // ── Audit ──

    [Fact]
    public async Task BulkGrant_WritesAnAuditRowPerGrant()
    {
        using var factory = CreateFactory($"bulk-grant-{Guid.NewGuid():N}");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(Token(factory), new
        {
            tenantId = MyTenant, employeeIds = new[] { AliceId, BobId }, permissions = "Schedule.Read",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BulkGrantResponse>();

        // Drained asynchronously by AuditBackgroundService.
        AppDbContext db = null!;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            db = Db(factory);
            if (db.AuditLogs.Count(a => a.ResourceType == "PermissionGrant") >= 2) break;
            await Task.Delay(100);
        }

        var rows = db.AuditLogs.Where(a => a.ResourceType == "PermissionGrant").ToList();
        rows.Should().HaveCountGreaterThanOrEqualTo(2);
        rows.Should().OnlyContain(a => a.Action == "Granted");
        rows.Should().OnlyContain(a => a.Details!.Contains(body!.BatchId.ToString()));
    }
}
