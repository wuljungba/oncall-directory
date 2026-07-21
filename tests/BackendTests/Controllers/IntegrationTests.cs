using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BackendTests.Controllers;

/// <summary>
/// Integration tests for unprotected endpoints (health check only).
/// Protected API endpoints require JWT auth and should be tested via service-level tests.
/// </summary>
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsJsonWithStatus()
    {
        var client = _factory.CreateClient();
        var content = await client.GetStringAsync("/health");

        content.Should().Contain("status");
        content.Should().Contain("Healthy");
    }
}
