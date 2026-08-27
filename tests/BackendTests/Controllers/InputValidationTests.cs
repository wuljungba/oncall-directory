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
/// Request DTOs must reject malformed input with a clean 400.
///
/// The tenant request records carried no data annotations at all. The Tenant entity does,
/// but [ApiController] validates the bound request type, so those never fired: an empty
/// name ("" is not null) or an oversized string reached SaveChanges, where SQL Server
/// throws a 500 and SQLite silently stores it.
///
/// There is no SQL injection surface here to test — the auth and tenant paths are entirely
/// LINQ-to-entities, and the single ExecuteSqlRawAsync in Program.cs concatenates no user
/// input — so these cover validation and identifier hygiene instead.
/// </summary>
[Collection(WebHostCollection.Name)]
public class InputValidationTests
{
    private const string SigningKey = "test-signing-key-for-input-validation-0123456789";
    private const string SuperAdminEmail = "boss@hospital.test";

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"input-validation-{Guid.NewGuid():N}";
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

    /// <summary>A configured super admin, so these requests reach the handler rather than a 403.</summary>
    private static string AdminToken(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(7, SuperAdminEmail, "The Boss", new[] { "OnCall.Viewer" });
    }

    private static async Task<HttpResponseMessage> Post(
        WebApplicationFactory<Program> factory, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken(factory));
        return await factory.CreateClient().SendAsync(request);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatingATenantWithABlankNameIsRejected(string name)
    {
        using var factory = CreateFactory();

        using var response = await Post(factory, "/api/tenants",
            new { Name = name, Description = (string?)null, AzureAdGroupId = (string?)null, ContactEmail = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatingATenantWithAnOversizedNameIsRejected()
    {
        using var factory = CreateFactory();

        using var response = await Post(factory, "/api/tenants",
            new { Name = new string('x', 500), ContactEmail = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Any().Should().BeFalse("a rejected request must not have stored anything");
    }

    [Fact]
    public async Task CreatingATenantWithAMalformedContactEmailIsRejected()
    {
        using var factory = CreateFactory();

        using var response = await Post(factory, "/api/tenants",
            new { Name = "Northwood Medical", ContactEmail = "notanemail" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatingAValidTenantSucceeds()
    {
        using var factory = CreateFactory();

        using var response = await Post(factory, "/api/tenants",
            new { Name = "Northwood Medical", ContactEmail = "ops@northwood.test" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>Names that differ only by case or padding are the same name to a human.</summary>
    [Fact]
    public async Task CreatingATenantWhoseNameDiffersOnlyByCaseIsRejected()
    {
        using var factory = CreateFactory();

        (await Post(factory, "/api/tenants", new { Name = "Northwood Medical" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        using var duplicate = await Post(factory, "/api/tenants", new { Name = "  northwood medical  " });

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Local account registration ─────────────────────────────────────────────

    /// <summary>
    /// A login identifier must be a real address. TenantClaimsMiddleware decides whether a
    /// PermissionGrant targets an email or an object id purely by whether it contains "@",
    /// so a non-email username carrying one would be matched against the wrong grants.
    /// </summary>
    [Theory]
    [InlineData("notanemail")]
    [InlineData("weird@user@name")]
    [InlineData("@leading")]
    public async Task RegisteringWithAMalformedEmailIsRejected(string email)
    {
        using var factory = CreateFactory();

        using var response = await Post(factory, "/api/auth/local/register",
            new { Email = email, Password = "a-good-password", DisplayName = "Someone" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RegisteringWithAShortPasswordIsRejected()
    {
        using var factory = CreateFactory();

        using var response = await Post(factory, "/api/auth/local/register",
            new { Email = "someone@hospital.test", Password = "short", DisplayName = "Someone" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
