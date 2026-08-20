using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Authentication;

namespace BackendTests.Controllers;

/// <summary>
/// Guards authentication of the notification hub's transports.
///
/// A browser cannot attach an Authorization header to a WebSocket handshake or an
/// EventSource request, so the SignalR client sends the token as an `access_token`
/// query parameter instead. When nothing read it, both transports failed auth and the
/// client silently degraded to long polling — which matters because this hub carries
/// code-call dispatch outcomes, including SMS delivery failures.
///
/// The other half of these tests is the part that must NOT regress: accepting a token
/// from a query string is scoped to /hubs only. Query strings leak into server and
/// proxy logs, so it must never become a way to authenticate ordinary API calls.
/// </summary>
[Collection(WebHostCollection.Name)]
public class SignalRHubAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    // 32+ chars: the local JWT signing key has a minimum length.
    private const string SigningKey = "test-signing-key-for-hub-auth-tests-0123456789";

    private readonly WebApplicationFactory<Program> _factory;

    public SignalRHubAuthTests(WebApplicationFactory<Program> factory)
    {
        // DevAuth would auto-authenticate every request and bypass the JWT pipeline
        // entirely, so it must be off for these tests to mean anything.
        //
        // It has to be set with UseSetting, not ConfigureAppConfiguration: Program.cs
        // reads DevAuth:Enabled eagerly while composing the builder, which happens
        // before ConfigureAppConfiguration callbacks are applied. (Options-bound
        // settings like Dispatch:* are fine either way, since they resolve lazily.)
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("DevAuth:Enabled", "false");
            b.UseSetting("Authentication:Local:SigningKey", SigningKey);
        });
    }

    private string MintLocalToken()
    {
        using var scope = _factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
        return jwt.GenerateToken(1, "hub-test@example.test", "Hub Test", new[] { "OnCall.Viewer" });
    }

    private static string NegotiatePath(string? accessToken) =>
        "/hubs/notifications/negotiate?negotiateVersion=1"
        + (accessToken == null ? "" : $"&access_token={Uri.EscapeDataString(accessToken)}");

    [Fact]
    public async Task HubNegotiate_WithTokenInQueryString_IsAuthenticated()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(NegotiatePath(MintLocalToken()), null);

        // The point: not 401. Without the query-string token the handshake is rejected
        // and the browser falls back to long polling.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HubNegotiate_WithTokenInAuthorizationHeader_StillWorks()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, NegotiatePath(null));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintLocalToken());

        var response = await client.SendAsync(request);

        // Long polling sends a header and must keep working.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HubNegotiate_WithoutAnyToken_IsRejected()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(NegotiatePath(null), null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HubNegotiate_WithMalformedQueryToken_IsRejected()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(NegotiatePath("not.a.valid.jwt"), null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HubNegotiate_WithTokenSignedByAnotherKey_IsRejected()
    {
        var client = _factory.CreateClient();

        // A well-formed token from a different issuer/key must not open the hub —
        // reading the token from the query string must not skip validation.
        var foreign = new LocalJwtService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Local:SigningKey"] = "a-completely-different-signing-key-9876543210",
            }).Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalJwtService>.Instance,
            _factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>());

        var response = await client.PostAsync(
            NegotiatePath(foreign.GenerateToken(2, "attacker@example.test", "Attacker", new[] { "OnCall.Admin" })),
            null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/directory/search?q=a&")]
    [InlineData("/api/schedule")]
    public async Task ApiEndpoints_DoNotAcceptTokenFromQueryString(string path)
    {
        var client = _factory.CreateClient();
        var sep = path.Contains('?') ? "" : "?";

        var response = await client.GetAsync($"{path}{sep}access_token={Uri.EscapeDataString(MintLocalToken())}");

        // Scoped to /hubs on purpose: tokens in URLs get logged, so this must never
        // become an alternative way to authenticate the API.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
