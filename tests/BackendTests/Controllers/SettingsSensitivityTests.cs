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
/// GET /api/settings is open to Schedule.Read — most of the directory — because it was built
/// for the default rotation and the dial-plan prefix. It returned every row in full, so the
/// Graph delta link written into the same table went with them: a resource URL that resumes a
/// customer's directory enumeration. The link has moved to its own table; this is the rule for
/// whatever gets written here next.
/// </summary>
[Collection(WebHostCollection.Name)]
public class SettingsSensitivityTests
{
    private const string SigningKey = "test-signing-key-for-settings-sensitivity-0123456789";
    private const string SuperAdminEmail = "boss@hospital.test";
    private const string SensitiveKey = "AdDeltaToken:1";
    private const string OrdinaryKey = "schedule.default_rotation";

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"settings-sensitivity-{Guid.NewGuid():N}";
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

    private static void Seed(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppSettings.Add(new AppSetting
        {
            Key = SensitiveKey,
            Value = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=secret-cursor",
            UpdatedAt = DateTime.UtcNow,
        });
        db.AppSettings.Add(new AppSetting { Key = OrdinaryKey, Value = "weekly", UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    private static async Task<HttpResponseMessage> Get(
        WebApplicationFactory<Program> factory, string path, string email)
    {
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(7, email, "Someone", new[] { "OnCall.Viewer" });
        }

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await factory.CreateClient().SendAsync(request);
    }

    private sealed record SettingRow(string Key, string? Value, string? Description, DateTime UpdatedAt, bool IsSensitive);

    [Fact]
    public async Task ASensitiveValueIsWithheldFromTheListAndSaysSo()
    {
        using var factory = CreateFactory();
        Seed(factory);

        using var response = await Get(factory, "/api/settings", SuperAdminEmail);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<SettingRow>>();
        rows.Should().NotBeNull();

        var sensitive = rows!.Single(r => r.Key == SensitiveKey);
        sensitive.Value.Should().BeNull("a Graph resource link is not a setting to hand round");
        sensitive.IsSensitive.Should().BeTrue("null must read as withheld, not as an empty setting");

        var ordinary = rows!.Single(r => r.Key == OrdinaryKey);
        ordinary.Value.Should().Be("weekly");
        ordinary.IsSensitive.Should().BeFalse();
    }

    [Fact]
    public async Task AFullAdminCanStillReadASensitiveValueDirectly()
    {
        using var factory = CreateFactory();
        Seed(factory);

        using var response = await Get(factory, $"/api/settings/{Uri.EscapeDataString(SensitiveKey)}", SuperAdminEmail);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await response.Content.ReadFromJsonAsync<SettingRow>();
        row!.Value.Should().Contain("deltatoken");
    }

    [Fact]
    public async Task AnOrdinaryReaderIsNotEvenToldTheSensitiveKeyExists()
    {
        using var factory = CreateFactory();
        Seed(factory);

        using var response = await Get(factory, $"/api/settings/{Uri.EscapeDataString(SensitiveKey)}", "reader@hospital.test");

        // 404 rather than 403: whether a particular secret is configured is itself a disclosure.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            (await response.Content.ReadAsStringAsync()).Should().NotContain("deltatoken");
        }
    }
}
