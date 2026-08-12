using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BackendTests.Controllers;

/// <summary>
/// Guards the EHR webhook consent/auth gate: an unauthenticated launch must be rejected
/// (never fire a code call). Uses the app's HealthEndpointTests factory pattern, with the
/// webhook key set to a test value so the auth path itself is exercised.
/// </summary>
public class EhrWebhookControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TestKey = "test-ehr-webhook-secret";
    private readonly WebApplicationFactory<Program> _factory;

    public EhrWebhookControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:EhrWebhook:Key"] = TestKey,
                })));
    }

    private HttpRequestMessage Build(string body, bool withKey = false, string? key = null, bool withHmac = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/public/ehr/on-call")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (withKey) req.Headers.Add("X-Ehr-Key", key ?? TestKey);
        if (withHmac)
        {
            var sig = Convert.ToBase64String(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestKey), Encoding.UTF8.GetBytes(body)));
            req.Headers.Add("X-Ehr-Signature", sig);
        }
        return req;
    }

    [Fact]
    public async Task LaunchOnCall_WithoutAuth_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.SendAsync(Build("{\"codeType\":\"code-blue\",\"location\":\"ED Bay 4\"}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LaunchOnCall_WithWrongKey_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.SendAsync(Build("{\"codeType\":\"code-blue\",\"location\":\"ED\"}", withKey: true, key: "wrong-key"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LaunchOnCall_WithValidHmac_PassesAuth()
    {
        var client = _factory.CreateClient();
        // Correct HMAC -> auth passes; then proceeds to phone-tree lookup (no tree in test
        // DB) -> 404. The point is it is NOT 401, proving the signature gate accepted it.
        var response = await client.SendAsync(Build("{\"codeType\":\"code-blue\",\"location\":\"ED\"}", withHmac: true));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}