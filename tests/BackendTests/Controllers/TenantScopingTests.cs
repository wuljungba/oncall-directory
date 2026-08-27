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

        // Staff, schedules and shifts in each tenant, so scoping can be checked on the
        // directory and schedule surfaces rather than departments alone.
        db.Employees.AddRange(
            new Employee
            {
                Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
                FirstName = "Ann", LastName = "Alpha", Email = "ann@a.test",
                AzureAdObjectId = "emp-a", TenantId = TenantA, DepartmentId = 10, IsActive = true,
            },
            new Employee
            {
                Id = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
                FirstName = "Ben", LastName = "Beta", Email = "ben@b.test",
                AzureAdObjectId = "emp-b", TenantId = TenantB, DepartmentId = 20, IsActive = true,
            });
        db.Schedules.AddRange(
            new Schedule { Id = 100, Name = "Cardiology call", DepartmentId = 10 },
            new Schedule { Id = 200, Name = "Neurology call", DepartmentId = 20 });

        var now = DateTime.UtcNow;
        db.Shifts.AddRange(
            new Shift
            {
                Id = 1000, ScheduleId = 100, EmployeeId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
                Tier = "primary", Status = "scheduled", StartTime = now.AddHours(-1), EndTime = now.AddHours(1),
            },
            new Shift
            {
                Id = 2000, ScheduleId = 200, EmployeeId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
                Tier = "primary", Status = "scheduled", StartTime = now.AddHours(-1), EndTime = now.AddHours(1),
            });

        db.DutyHourRules.AddRange(
            new DutyHourRule { Id = 500, Name = "Cardiology 80h", DepartmentId = 10, IsEnabled = true },
            new DutyHourRule { Id = 600, Name = "Neurology 80h", DepartmentId = 20, IsEnabled = true });
        db.CodeCallLocations.AddRange(
            new CodeCallLocation { Id = 700, Name = "A-Ward-3", DepartmentId = 10, IsActive = true },
            new CodeCallLocation { Id = 800, Name = "B-Ward-9", DepartmentId = 20, IsActive = true });

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

    // ── Beyond /api/departments ────────────────────────────────────────────────
    // Only the departments path went through AdminService, the one service that scoped.
    // The directory and schedule surfaces had no tenant filtering at all, so these
    // endpoints returned every tenant's data to any holder of the matching permission —
    // and a suite that only exercised departments reported all green.

    private static async Task<List<T>?> GetAs<T>(HttpClient client, string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<List<T>>();
    }

    [Fact]
    public async Task DirectorySearch_IsScopedToTheCallersTenant()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        var employees = await GetAs<Employee>(client, UserToken(factory), "/api/directory/search?q=");

        employees!.Select(e => e.LastName).Should().BeEquivalentTo("Alpha");
    }

    [Fact]
    public async Task OnCallDirectory_IsScopedToTheCallersTenant()
    {
        using var factory = Seed(grantTenantId: TenantB);
        using var client = factory.CreateClient();

        var employees = await GetAs<Employee>(client, UserToken(factory), "/api/directory/search?q=");

        employees!.Select(e => e.LastName).Should().BeEquivalentTo("Beta");
    }

    [Fact]
    public async Task Schedules_AreScopedToTheCallersTenant()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        var schedules = await GetAs<Schedule>(client, UserToken(factory), "/api/schedule");

        schedules!.Select(s => s.Name).Should().BeEquivalentTo("Cardiology call");
    }

    [Fact]
    public async Task CurrentOnCallRoster_IsScopedToTheCallersTenant()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        var shifts = await GetAs<Shift>(client, UserToken(factory), "/api/schedule/on-call");

        shifts!.Select(s => s.ScheduleId).Should().BeEquivalentTo([100]);
    }

    [Fact]
    public async Task SystemWideGrant_StillSeesEveryTenantsDirectory()
    {
        using var factory = Seed(grantTenantId: null, grantSystemWide: true);
        using var client = factory.CreateClient();

        var employees = await GetAs<Employee>(client, UserToken(factory), "/api/directory/search?q=");

        employees!.Select(e => e.LastName).Should().BeEquivalentTo("Alpha", "Beta");
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

    // ── Compliance ─────────────────────────────────────────────────────────────
    // DutyHourService had no tenant filter at all, and departmentId is optional: omitting
    // it returned every tenant's rules and swept every tenant's staff into the check.
    // CheckComplianceAsync also PERSISTS violations, so this was a cross-tenant write.

    [Fact]
    public async Task ComplianceRules_AreScopedToTheCallersTenant()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        var rules = await GetAs<DutyHourRule>(client, UserToken(factory), "/api/compliance/rules");

        rules!.Select(r => r.Name).Should().BeEquivalentTo("Cardiology 80h");
    }

    [Fact]
    public async Task ComplianceCheck_DoesNotEvaluateAnotherTenantsStaff()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        var violations = await GetAs<DutyHourViolation>(client, UserToken(factory), "/api/compliance/check");

        // The seeded shifts are short, so the sweep may legitimately find nothing. What
        // must hold either way is that it never reached tenant B's staff.
        violations!.Should().NotContain(
            v => v.EmployeeId == Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
            "a compliance sweep must not reach into another tenant's staff");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DutyHourViolations.Any(v => v.EmployeeId == Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"))
            .Should().BeFalse("the sweep must not write to another tenant's compliance record either");
    }

    [Fact]
    public async Task ComplianceCheck_ForAnotherTenantsEmployee_WritesNoViolation()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();
        var benId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/compliance/check/{benId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UserToken(factory));
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await response.Content.ReadFromJsonAsync<List<DutyHourViolation>>())!.Should().BeEmpty();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DutyHourViolations.Any(v => v.EmployeeId == benId)
            .Should().BeFalse("evaluating an out-of-scope employee must not write to their compliance record");
    }

    // ── Single-item endpoints ──────────────────────────────────────────────────
    // The tenant filter lived only in the list endpoints, so one id read — or rewrote —
    // any other tenant's record.

    private static async Task<HttpResponseMessage> SendAs(
        HttpClient client, string token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task DepartmentById_FromAnotherTenant_IsNotFound()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await SendAs(client, UserToken(factory), HttpMethod.Get, "/api/departments/20");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DepartmentById_InOwnTenant_IsReturned()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await SendAs(client, UserToken(factory), HttpMethod.Get, "/api/departments/10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CodeCallLocationById_FromAnotherTenant_IsNotFound()
    {
        using var factory = Seed(grantTenantId: TenantA);
        using var client = factory.CreateClient();

        using var response = await SendAs(client, UserToken(factory), HttpMethod.Get, "/api/code-call-locations/800");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
