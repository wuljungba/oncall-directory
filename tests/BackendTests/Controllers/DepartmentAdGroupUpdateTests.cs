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
/// A department's Entra group can be changed after it is created.
///
/// CreateDepartmentRequest carried AzureAdGroupId and UpdateDepartmentRequest did not, so the
/// group could be set once and never corrected — and it is what DepartmentSyncService matches
/// a Graph group on, so a wrong value meant syncing the wrong people, fixable only in the
/// database.
///
/// Null and empty mean different things on purpose: leave it alone, versus detach it.
/// </summary>
[Collection(WebHostCollection.Name)]
public class DepartmentAdGroupUpdateTests
{
    private const string SigningKey = "test-signing-key-for-department-ad-group-0123456789";
    private const string SuperAdminEmail = "boss@hospital.test";
    private const int DepartmentId = 5001;

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"dept-ad-group-{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
    }

    private static void SeedDepartment(WebApplicationFactory<Program> factory, string? groupId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Departments.Add(new Department
        {
            Id = DepartmentId,
            Name = "Cardiology",
            AzureAdGroupId = groupId,
            IsActive = true,
        });
        db.SaveChanges();
    }

    private static string? ReadGroupId(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Departments.AsNoTracking().Single(d => d.Id == DepartmentId).AzureAdGroupId;
    }

    private static async Task<HttpResponseMessage> Update(WebApplicationFactory<Program> factory, object body)
    {
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(7, SuperAdminEmail, "The Boss", new[] { "OnCall.Viewer" });
        }

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/departments/{DepartmentId}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task AnAdGroupCanBeAttachedToADepartmentThatHadNone()
    {
        using var factory = CreateFactory();
        SeedDepartment(factory, groupId: null);

        using var response = await Update(factory, new { Name = "Cardiology", AzureAdGroupId = "group-abc" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadGroupId(factory).Should().Be("group-abc");
    }

    [Fact]
    public async Task ACorrectedAdGroupReplacesTheWrongOne()
    {
        using var factory = CreateFactory();
        SeedDepartment(factory, groupId: "group-wrong");

        using var response = await Update(factory, new { Name = "Cardiology", AzureAdGroupId = "  group-right  " });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadGroupId(factory).Should().Be("group-right", "a pasted id carries whitespace");
    }

    /// <summary>An update that says nothing about the group must not silently detach it.</summary>
    [Fact]
    public async Task OmittingTheAdGroupLeavesItAlone()
    {
        using var factory = CreateFactory();
        SeedDepartment(factory, groupId: "group-keep");

        using var response = await Update(factory, new { Name = "Cardiology renamed" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadGroupId(factory).Should().Be("group-keep");
    }

    [Fact]
    public async Task AnEmptyAdGroupDetachesTheDepartmentFromItsGroup()
    {
        using var factory = CreateFactory();
        SeedDepartment(factory, groupId: "group-remove-me");

        using var response = await Update(factory, new { Name = "Cardiology", AzureAdGroupId = "" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadGroupId(factory).Should().BeNull();
    }
}
