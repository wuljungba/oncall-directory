using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Data;
using OnCallApi.Models;

namespace BackendTests.Controllers;

/// <summary>
/// A public share link is an anonymous, unauthenticated door into a tenant's coverage
/// view. Revocation used to be entirely manual, so a link shared with a rotating group
/// stayed live until somebody remembered it existed.
/// </summary>
[Collection(WebHostCollection.Name)]
public class PublicShareExpiryTests
{
    private static readonly Guid LiveToken = Guid.NewGuid();
    private static readonly Guid ExpiredToken = Guid.NewGuid();
    private static readonly Guid DisabledToken = Guid.NewGuid();

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbName = $"public-share-{Guid.NewGuid():N}";
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(new Tenant { Id = 1, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.PublicShares.AddRange(
            new PublicShare { Id = 1, TenantId = 1, Token = LiveToken, Label = "Live", IsActive = true, ExpiresAt = DateTime.UtcNow.AddDays(7) },
            new PublicShare { Id = 2, TenantId = 1, Token = ExpiredToken, Label = "Expired", IsActive = true, ExpiresAt = DateTime.UtcNow.AddDays(-1) },
            new PublicShare { Id = 3, TenantId = 1, Token = DisabledToken, Label = "Disabled", IsActive = false });
        db.SaveChanges();

        return factory;
    }

    [Fact]
    public async Task UnexpiredLink_Resolves()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/public/schedule/on-call/{LiveToken}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExpiredLink_StopsResolving()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/public/schedule/on-call/{ExpiredToken}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DisabledLink_StopsResolving()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/public/schedule/on-call/{DisabledToken}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownExpiredAndDisabledAreIndistinguishable()
    {
        // An anonymous caller should learn only that the link does not work — not whether
        // it was ever real.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var bodies = new List<string>();
        foreach (var token in new[] { ExpiredToken, DisabledToken, Guid.NewGuid() })
        {
            using var response = await client.GetAsync($"/api/public/schedule/on-call/{token}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        bodies.Distinct().Should().ContainSingle("the three cases must not be tellable apart");
    }

    [Fact]
    public void ShareWithNoExpiry_NeverExpires()
    {
        var share = new PublicShare { IsActive = true, ExpiresAt = null };

        share.IsUsable(DateTime.UtcNow.AddYears(5)).Should().BeTrue();
    }
}
