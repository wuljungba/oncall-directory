using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Data;
using OnCallApi.Models;

namespace BackendTests.Controllers;

/// <summary>
/// The public coverage endpoint, exercised against a RELATIONAL provider.
///
/// PublicShareExpiryTests already asserts that a live link returns 200, and it passed
/// throughout the whole time this endpoint answered 500 to every real caller. It runs on
/// the in-memory provider, which executes LINQ against objects and never translates it to
/// SQL — so `Tier.ToLowerInvariant()` inside a GroupBy worked there and threw
/// InvalidOperationException everywhere else.
///
/// That is a whole class of defect the in-memory suite cannot see. These run on SQLite so
/// the query is really translated, and they matter most here because this is the one
/// endpoint with no authentication in front of it: nobody signs in and notices it broken.
/// </summary>
[Collection(WebHostCollection.Name)]
public class PublicShareCoverageTests
{
    private static readonly Guid ShareToken = Guid.NewGuid();

    private static (WebApplicationFactory<Program> Factory, SqliteConnection Connection) CreateFactory()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
            });
        });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;

        // The application seeds its own tenant and departments during startup, so these
        // use ids well clear of them and reuse whatever tenant is already there.
        var tenantId = db.Tenants.Select(t => t.Id).FirstOrDefault();
        if (tenantId == 0)
        {
            var seeded = new Tenant { Name = "Main Hospital", IsActive = true, CreatedAt = now };
            db.Tenants.Add(seeded);
            db.SaveChanges();
            tenantId = seeded.Id;
        }

        var department = new Department
        {
            Name = "CoverageCardiology", TenantId = tenantId, IsActive = true,
        };
        db.Departments.Add(department);
        db.SaveChanges();

        var employee = new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = "cover-1",
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.test",
            DepartmentId = department.Id,
            TenantId = tenantId,
            IsActive = true,
        };
        db.Employees.Add(employee);

        var schedule = new Schedule
        {
            Name = "Cardiology rota",
            DepartmentId = department.Id,
            StartDate = now.AddDays(-1),
            EndDate = now.AddDays(7),
            IsActive = true,
        };
        db.Schedules.Add(schedule);
        db.SaveChanges();

        // Mixed case on purpose: the lowercasing in the GroupBy is what broke, so the
        // rows have to be able to collide if it stops happening.
        db.Shifts.AddRange(
            new Shift
            {
                ScheduleId = schedule.Id, EmployeeId = employee.Id, Tier = "Primary",
                StartTime = now.AddHours(-1), EndTime = now.AddHours(6), Status = "scheduled",
            },
            new Shift
            {
                ScheduleId = schedule.Id, EmployeeId = employee.Id, Tier = "primary",
                StartTime = now.AddHours(-1), EndTime = now.AddHours(6), Status = "scheduled",
            });

        db.PublicShares.Add(new PublicShare
        {
            TenantId = tenantId, Token = ShareToken, Label = "Live",
            IsActive = true, ExpiresAt = now.AddDays(7),
        });
        db.SaveChanges();

        return (factory, connection);
    }

    [Fact]
    public async Task ALiveLinkReturnsCoverage_NotAnInternalError()
    {
        var (factory, connection) = CreateFactory();
        try
        {
            using var client = factory.CreateClient();

            using var response = await client.GetAsync($"/api/public/schedule/on-call/{ShareToken}");

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "this endpoint is anonymous — when it breaks, no signed-in user hits it and nobody finds out");

            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("CoverageCardiology");
        }
        finally
        {
            factory.Dispose();
            connection.Dispose();
        }
    }

    /// <summary>
    /// Coverage only. The link is handed to people outside the organization, so a name,
    /// an address or a phone number reaching it is a disclosure, not a feature.
    /// </summary>
    [Fact]
    public async Task ALiveLinkNeverExposesWhoIsOnCall()
    {
        var (factory, connection) = CreateFactory();
        try
        {
            using var client = factory.CreateClient();

            using var response = await client.GetAsync($"/api/public/schedule/on-call/{ShareToken}");
            var body = await response.Content.ReadAsStringAsync();

            // Assert the success first: an error body contains no names either, and
            // without this the test would pass just as happily against a 500.
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            body.Should().NotContain("Lovelace");
            body.Should().NotContain("ada@example.test");
        }
        finally
        {
            factory.Dispose();
            connection.Dispose();
        }
    }
}
