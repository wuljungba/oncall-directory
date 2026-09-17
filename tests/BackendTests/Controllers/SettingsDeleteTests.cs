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
/// A setting can be removed, not only overwritten.
///
/// The controller could upsert but never delete, and an absent row is what makes the code
/// fall back to its built-in default — so a setting written by mistake could only be replaced
/// with another value, never undone.
/// </summary>
[Collection(WebHostCollection.Name)]
public class SettingsDeleteTests
{
    private const string SigningKey = "test-signing-key-for-settings-delete-0123456789";
    private const string SuperAdminEmail = "boss@hospital.test";
    private const string Key = "schedule.default_rotation";

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"settings-delete-{Guid.NewGuid():N}";
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

    private static void SeedSetting(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppSettings.Add(new AppSetting { Key = Key, Value = "weekly", UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    private static bool SettingExists(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.AppSettings.AsNoTracking().Any(s => s.Key == Key);
    }

    private static async Task<HttpResponseMessage> Delete(
        WebApplicationFactory<Program> factory, string key, string email)
    {
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(7, email, "Someone", new[] { "OnCall.Viewer" });
        }

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/settings/{key}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task DeletingASettingRestoresTheBuiltInDefaultByRemovingTheRow()
    {
        using var factory = CreateFactory();
        SeedSetting(factory);

        using var response = await Delete(factory, Key, SuperAdminEmail);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        SettingExists(factory).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingASettingThatWasNeverSetIsNotFound()
    {
        using var factory = CreateFactory();

        using var response = await Delete(factory, "never.set", SuperAdminEmail);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ACallerWhoIsNotAFullAdminCannotDeleteASetting()
    {
        using var factory = CreateFactory();
        SeedSetting(factory);

        using var response = await Delete(factory, Key, "someone@hospital.test");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        SettingExists(factory).Should().BeTrue("a refused delete must leave the setting in place");
    }
}
