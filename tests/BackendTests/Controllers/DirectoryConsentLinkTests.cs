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
/// The consent links an operator sends a connected organization.
///
/// There are two app registrations to consent to, and the link used to cover only OnCall
/// Graph. A customer directory that does not let users consent to apps — the norm at
/// hospitals — then connected cleanly while every one of its staff was stopped at
/// "Need admin approval" for OnCall API, the app they actually sign in to.
/// </summary>
[Collection(WebHostCollection.Name)]
public class DirectoryConsentLinkTests
{
    private const string SigningKey = "test-signing-key-for-consent-links-0123456789";
    private const string SuperAdminEmail = "boss@hospital.test";
    private const string SignInClientId = "11111111-1111-1111-1111-111111111111";
    private const string DirectoryClientId = "22222222-2222-2222-2222-222222222222";
    private const string CustomerDirectoryId = "33333333-3333-3333-3333-333333333333";
    private const string Origin = "https://oncall.test";
    private const int TenantId = 9001;

    private record ConsentLinks(
        string DirectoryTenantId, string RedirectUri, string SignInConsentUrl, string DirectoryConsentUrl, string Note);

    private record ErrorBody(string Error);

    private static WebApplicationFactory<Program> CreateFactory(string signInClientId = SignInClientId)
    {
        var dbName = $"consent-links-{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("DevAuth:Enabled", "false");
            builder.UseSetting("Authentication:Local:SigningKey", SigningKey);
            builder.UseSetting("Authentication:SuperAdmins:Emails:0", SuperAdminEmail);
            builder.UseSetting("AzureAd:ClientId", signInClientId);
            builder.UseSetting("GraphApi:ClientId", DirectoryClientId);
            builder.UseSetting("Cors:Origin", Origin);
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });
    }

    private static void SeedTenant(WebApplicationFactory<Program> factory, string? directoryTenantId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Northwood Medical", AzureAdTenantId = directoryTenantId });
        db.SaveChanges();
    }

    private static async Task<HttpResponseMessage> GetLinks(WebApplicationFactory<Program> factory, string email)
    {
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(7, email, "Someone", new[] { "OnCall.Viewer" });
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tenants/{TenantId}/directory-consent-link");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task BothAppRegistrationsGetAConsentLinkForTheCustomersDirectory()
    {
        using var factory = CreateFactory();
        SeedTenant(factory, CustomerDirectoryId);

        using var response = await GetLinks(factory, SuperAdminEmail);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var links = await response.Content.ReadFromJsonAsync<ConsentLinks>();
        links.Should().NotBeNull();

        var consentBase = $"https://login.microsoftonline.com/{CustomerDirectoryId}/adminconsent?";
        var redirect = $"redirect_uri={Uri.EscapeDataString($"{Origin}/admin")}";

        links!.SignInConsentUrl.Should().StartWith(consentBase)
            .And.Contain($"client_id={SignInClientId}", "the sign-in link must approve OnCall API")
            .And.Contain(redirect);
        links.DirectoryConsentUrl.Should().StartWith(consentBase)
            .And.Contain($"client_id={DirectoryClientId}", "the directory link must approve OnCall Graph")
            .And.Contain(redirect);
        links.RedirectUri.Should().Be($"{Origin}/admin");
        links.DirectoryTenantId.Should().Be(CustomerDirectoryId);
    }

    [Fact]
    public async Task ASubscriptionWithNoConnectedDirectoryGetsNoLinks()
    {
        using var factory = CreateFactory();
        SeedTenant(factory, directoryTenantId: null);

        using var response = await GetLinks(factory, SuperAdminEmail);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnUnconfiguredSignInAppIsNamedRatherThanLinkedToAPlaceholder()
    {
        using var factory = CreateFactory(signInClientId: "your-api-client-id");
        SeedTenant(factory, CustomerDirectoryId);

        using var response = await GetLinks(factory, SuperAdminEmail);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        body!.Error.Should().Contain("AzureAd:ClientId");
    }

    [Fact]
    public async Task ACallerWhoCannotManageSubscriptionsIsRefused()
    {
        using var factory = CreateFactory();
        SeedTenant(factory, CustomerDirectoryId);

        using var response = await GetLinks(factory, "someone@hospital.test");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
