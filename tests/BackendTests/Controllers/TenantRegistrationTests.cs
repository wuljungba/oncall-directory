using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BackendTests.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Authentication;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Controllers;

/// <summary>
/// Connecting a customer's directory to a subscription.
///
/// The old flow was an operator typing the customer's Entra tenant GUID into a form, which fails
/// silently: a wrong id matches nobody's token, so the subscription reads as connected and their
/// staff simply cannot sign in. The consent redirect carried the real id all along and was never
/// used, because an id in a redirect is attacker-supplied.
///
/// These pin both halves of what replaced it — the invite says which subscription, and a live
/// Graph read says the consent was real — and, just as importantly, what happens when either half
/// is missing.
/// </summary>
[Collection(WebHostCollection.Name)]
public class TenantRegistrationTests
{
    private const string SigningKey = "test-signing-key-for-tenant-registration-0123456789";
    private const string SuperAdminEmail = "boss@hospital.test";
    private const int TenantId = 4001;
    private const string CustomerDirectory = "bbbbbbbb-1111-2222-3333-cccccccccccc";

    private static WebApplicationFactory<Program> CreateFactory(FakeGraphApiService graph)
    {
        var dbName = $"tenant-registration-{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("DevAuth:Enabled", "false");
            builder.UseSetting("Authentication:Local:SigningKey", SigningKey);
            builder.UseSetting("Authentication:SuperAdmins:Emails:0", SuperAdminEmail);
            builder.UseSetting("AzureAd:ClientId", "11111111-1111-1111-1111-111111111111");
            builder.UseSetting("GraphApi:ClientId", "22222222-2222-2222-2222-222222222222");
            builder.UseSetting("Cors:Origin", "https://oncall.test");
            builder.ConfigureServices(services =>
            {
                var db = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (db != null) services.Remove(db);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));

                var graphDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IGraphApiService));
                if (graphDescriptor != null) services.Remove(graphDescriptor);
                services.AddSingleton<IGraphApiService>(graph);
            });
        });
    }

    private static void SeedTenant(WebApplicationFactory<Program> factory, string? directoryId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "Northwood Medical",
            IsActive = true,
            AzureAdTenantId = directoryId,
        });
        db.SaveChanges();
    }

    private static string Token(WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(7, email, "Someone", new[] { "OnCall.Viewer" });
    }

    private static async Task<HttpResponseMessage> CreateInvite(
        WebApplicationFactory<Program> factory, string email = SuperAdminEmail)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{TenantId}/onboarding-invite");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(factory, email));
        return await factory.CreateClient().SendAsync(request);
    }

    private static Task<HttpResponseMessage> CompleteConsent(
        WebApplicationFactory<Program> factory, object body) =>
        factory.CreateClient().PostAsJsonAsync("/api/public/consent/complete", body);

    private sealed record InviteResponse(DateTime ExpiresAt, string SignInConsentUrl, string DirectoryConsentUrl, string Note);
    private sealed record CallbackResponse(bool Connected, string Message, string? SubscriptionName, bool? NeedsReconsent);

    private static async Task<Guid> IssueInviteToken(WebApplicationFactory<Program> factory)
    {
        using var response = await CreateInvite(factory);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.TenantOnboardingInvites.AsNoTracking().SingleAsync()).Token;
    }

    private static async Task<Tenant> Reload(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == TenantId);
    }

    // ── Issuing the invite ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInviteNamesNoDirectoryAndCarriesItsOwnToken()
    {
        var graph = new FakeGraphApiService();
        using var factory = CreateFactory(graph);
        SeedTenant(factory);

        using var response = await CreateInvite(factory);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var invite = await response.Content.ReadFromJsonAsync<InviteResponse>();

        // "organizations", not a tenant id: which directory it is, is precisely what we do not
        // know yet and are asking the customer's admin to tell us by signing in.
        invite!.SignInConsentUrl.Should().Contain("/organizations/adminconsent");
        invite.SignInConsentUrl.Should().Contain("state=");
        invite.DirectoryConsentUrl.Should().Contain("state=");
        invite.SignInConsentUrl.Should().NotBe(invite.DirectoryConsentUrl, "they consent to different apps");
    }

    [Fact]
    public async Task IssuingAnInviteNeedsSubscriptionManagement()
    {
        var graph = new FakeGraphApiService();
        using var factory = CreateFactory(graph);
        SeedTenant(factory);

        using var response = await CreateInvite(factory, "someone@hospital.test");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Completing it ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AConsentBackedByAReadableDirectoryConnectsIt()
    {
        var graph = new FakeGraphApiService { Organization = new DirectoryOrganization(
            "Northwood Medical Group", ["northwood.test"], NeedsReconsent: false, null) };
        using var factory = CreateFactory(graph);
        SeedTenant(factory);
        var token = await IssueInviteToken(factory);

        using var response = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = CustomerDirectory });

        var result = await response.Content.ReadFromJsonAsync<CallbackResponse>();
        result!.Connected.Should().BeTrue();

        var tenant = await Reload(factory);
        tenant.AzureAdTenantId.Should().Be(CustomerDirectory, "filled in by the callback, not typed by hand");
        tenant.DirectoryDisplayName.Should().Be("Northwood Medical Group");
        tenant.DirectoryVerifiedAt.Should().NotBeNull();

        graph.ProbeCalls.Should().Contain(CustomerDirectory, "the redirect's claim has to be checked against Graph");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditLogs.Any(a => a.ResourceType == "Tenant" && a.Details!.Contains("DirectoryConnected")).Should()
            .BeTrue("connecting a directory grants read access to everyone in it");
    }

    /// <summary>The whole point of the probe: a claim alone must not connect anything.</summary>
    [Fact]
    public async Task AConsentWeCannotVerifyConnectsNothing()
    {
        var graph = new FakeGraphApiService
        {
            DefaultProbe = new DirectoryProbeResult(CanRead: false, NeedsConsent: true, "AADSTS700016"),
        };
        using var factory = CreateFactory(graph);
        SeedTenant(factory);
        var token = await IssueInviteToken(factory);

        using var response = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = CustomerDirectory });

        var result = await response.Content.ReadFromJsonAsync<CallbackResponse>();
        result!.Connected.Should().BeFalse();
        result.Message.Should().Contain("cannot read your directory");

        (await Reload(factory)).AzureAdTenantId.Should().BeNull();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invite = await db.TenantOnboardingInvites.AsNoTracking().SingleAsync();
        invite.ConsumedAt.Should().BeNull("a failed attempt must not burn the invite");
        invite.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AnInviteCannotBeUsedTwice()
    {
        var graph = new FakeGraphApiService();
        using var factory = CreateFactory(graph);
        SeedTenant(factory);
        var token = await IssueInviteToken(factory);

        using var first = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = CustomerDirectory });
        (await first.Content.ReadFromJsonAsync<CallbackResponse>())!.Connected.Should().BeTrue();

        // A replay points the same invite at a different directory.
        using var replay = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = "dddddddd-9999-8888-7777-666666666666" });

        var result = await replay.Content.ReadFromJsonAsync<CallbackResponse>();
        result!.Connected.Should().BeFalse();
        (await Reload(factory)).AzureAdTenantId.Should().Be(CustomerDirectory, "the first connection stands");
    }

    /// <summary>
    /// A consumed invite and an invite that never existed must be indistinguishable, or the
    /// endpoint becomes a way to test which tokens are real.
    /// </summary>
    [Fact]
    public async Task AnUnknownTokenAnswersExactlyLikeAUsedOne()
    {
        var graph = new FakeGraphApiService();
        using var factory = CreateFactory(graph);
        SeedTenant(factory);
        var token = await IssueInviteToken(factory);

        using var used = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = CustomerDirectory });
        await used.Content.ReadFromJsonAsync<CallbackResponse>();

        using var replayed = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = CustomerDirectory });
        using var invented = await CompleteConsent(factory, new { state = Guid.NewGuid().ToString("N"), tenant = CustomerDirectory });

        var replayedBody = await replayed.Content.ReadFromJsonAsync<CallbackResponse>();
        var inventedBody = await invented.Content.ReadFromJsonAsync<CallbackResponse>();

        inventedBody!.Message.Should().Be(replayedBody!.Message);
    }

    [Fact]
    public async Task ADirectoryAlreadyConnectedElsewhereIsRefused()
    {
        var graph = new FakeGraphApiService();
        using var factory = CreateFactory(graph);
        SeedTenant(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Add(new Tenant
            {
                Id = 4002, Name = "Someone Else", IsActive = true, AzureAdTenantId = CustomerDirectory,
            });
            db.SaveChanges();
        }

        var token = await IssueInviteToken(factory);
        using var response = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = CustomerDirectory });

        var result = await response.Content.ReadFromJsonAsync<CallbackResponse>();
        result!.Connected.Should().BeFalse();
        result.Message.Should().Contain("already connected");
        (await Reload(factory)).AzureAdTenantId.Should().BeNull();
    }

    /// <summary>
    /// Re-pointing a live subscription at a different directory withdraws access from everyone
    /// in the old one. That is too large a consequence for a side effect of opening a link.
    /// </summary>
    [Fact]
    public async Task ASubscriptionAlreadyConnectedElsewhereIsNotRePointed()
    {
        const string Existing = "eeeeeeee-4444-5555-6666-777777777777";
        var graph = new FakeGraphApiService();
        using var factory = CreateFactory(graph);
        SeedTenant(factory, Existing);
        var token = await IssueInviteToken(factory);

        using var response = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = CustomerDirectory });

        var result = await response.Content.ReadFromJsonAsync<CallbackResponse>();
        result!.Connected.Should().BeFalse();
        result.Message.Should().Contain("already connected to a different directory");

        (await Reload(factory)).AzureAdTenantId.Should().Be(Existing, "the directory in place stands");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.TenantOnboardingInvites.AsNoTracking().SingleAsync()).ConsumedAt
            .Should().BeNull("a refusal must not burn the invite");
    }

    /// <summary>Consenting again for the SAME directory is a refresh, not a re-point.</summary>
    [Fact]
    public async Task ReConsentingForTheSameDirectoryIsStillAccepted()
    {
        var graph = new FakeGraphApiService { Organization = new DirectoryOrganization(
            "Northwood Medical Group", ["northwood.test"], NeedsReconsent: false, null) };
        using var factory = CreateFactory(graph);
        SeedTenant(factory, CustomerDirectory);
        var token = await IssueInviteToken(factory);

        using var response = await CompleteConsent(factory, new { state = token.ToString("N"), tenant = CustomerDirectory });

        (await response.Content.ReadFromJsonAsync<CallbackResponse>())!.Connected.Should().BeTrue();

        var tenant = await Reload(factory);
        tenant.DirectoryDisplayName.Should().Be("Northwood Medical Group");
        tenant.DirectoryVerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ConsentThatMicrosoftRefusedChangesNothing()
    {
        var graph = new FakeGraphApiService();
        using var factory = CreateFactory(graph);
        SeedTenant(factory);
        var token = await IssueInviteToken(factory);

        using var response = await CompleteConsent(factory, new
        {
            state = token.ToString("N"),
            tenant = CustomerDirectory,
            error = "access_denied",
            errorDescription = "The admin cancelled.",
        });

        (await response.Content.ReadFromJsonAsync<CallbackResponse>())!.Connected.Should().BeFalse();
        (await Reload(factory)).AzureAdTenantId.Should().BeNull();
        graph.ProbeCalls.Should().BeEmpty("a refusal needs no Graph call at all");
    }

    // ── Status ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StatusReportsAConnectedDirectoryAsReadableOrNot()
    {
        var graph = new FakeGraphApiService
        {
            DefaultProbe = new DirectoryProbeResult(CanRead: false, NeedsConsent: true, "not consented"),
        };
        using var factory = CreateFactory(graph);
        SeedTenant(factory, CustomerDirectory);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tenants/{TenantId}/directory-status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(factory, SuperAdminEmail));
        using var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"canReadDirectory\":false",
            "a typed GUID is not evidence that anybody consented");
    }
}
