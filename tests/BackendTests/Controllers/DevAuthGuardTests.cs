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

namespace BackendTests.Controllers;

/// <summary>
/// Dev auth must be impossible to switch on outside Development, and impossible to mistake
/// for a real session when it is on.
///
/// When DevAuth:Enabled is true, Program.cs never registers the Microsoft, Google or Local
/// bearer schemes, JwtValidationMiddleware is skipped, and DevelopmentAuthenticationHandler
/// authenticates every request from a cookie that defaults to full admin — ignoring any
/// bearer token presented. That is a complete authentication bypass sitting behind one
/// configuration value, and nothing used to prevent it being set in a deployed environment
/// or report it in the UI when it was set locally.
///
/// This is what produced the original report of a brand-new Google account appearing with
/// "All Tenants" super admin: the account never authenticated at all.
/// </summary>
[Collection(WebHostCollection.Name)]
public class DevAuthGuardTests
{
    private const string SigningKey = "test-signing-key-for-dev-auth-guard-0123456789";

    private static WebApplicationFactory<Program> CreateFactory(string environment, bool devAuth)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // UseSetting, not ConfigureAppConfiguration: Program.cs reads DevAuth eagerly.
            builder.UseSetting("DevAuth:Enabled", devAuth ? "true" : "false");
            // Set so the unrelated production signing-key guard cannot be what throws.
            builder.UseSetting("Authentication:Local:SigningKey", SigningKey);
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase($"devauth-{Guid.NewGuid():N}"));
            });
        });
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void EnablingDevAuthOutsideDevelopmentRefusesToStart(string environment)
    {
        using var factory = CreateFactory(environment, devAuth: true);

        // The host is built lazily, so the guard fires on first use.
        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>()
            .Where(e => Flatten(e).Contains("DevAuth", StringComparison.OrdinalIgnoreCase),
                "startup must refuse dev auth outside Development, naming it as the reason");
    }

    [Fact]
    public void DevelopmentWithDevAuthOffStartsNormally()
    {
        using var factory = CreateFactory("Development", devAuth: false);

        var act = () => factory.CreateClient();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DevAuthEndpointsAreAbsentWhenDevAuthIsOff()
    {
        using var factory = CreateFactory("Development", devAuth: false);
        using var client = factory.CreateClient();

        // Anonymous by design, so this is reachable without a token — it must simply not
        // exist in a build that does not honour the cookies it sets.
        using var response = await client.PostAsync("/api/auth/dev/set-role?role=admin", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The route must be absent even when the request would not bind.
    ///
    /// The guard was an action filter, which runs *after* model binding, so
    /// <c>[ApiController]</c>'s automatic model-state validation answered first: a
    /// production sweep got 400 with <c>errors: {"role": [...]}</c> from an endpoint that
    /// is supposed to be invisible. Not privilege escalation — the guard still fires once
    /// binding succeeds, and no cookie is ever set — but it advertises both the route and
    /// the parameter it wants, in exactly the build that must not have it.
    ///
    /// The test above passes <c>?role=admin</c>, which binds, so it never saw this.
    /// </summary>
    [Theory]
    [InlineData("/api/auth/dev/set-role")]        // no query string at all
    [InlineData("/api/auth/dev/set-role?role=")]  // present but empty
    [InlineData("/api/auth/dev/set-oid")]
    [InlineData("/api/auth/dev/set-oid?oid=")]
    public async Task DevAuthEndpointsAreAbsentEvenWhenTheRequestWouldNotBind(string url)
    {
        using var factory = CreateFactory("Development", devAuth: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(url, null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an endpoint absent from this build must not answer with a model-binding error "
            + "that names its parameters");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("role", "a 404 must not disclose the parameter name");
        body.Should().NotContain("oid", "a 404 must not disclose the parameter name");
    }

    [Fact]
    public async Task AuthMeReportsProductionAuthModeWhenDevAuthIsOff()
    {
        using var factory = CreateFactory("Development", devAuth: false);
        using var client = factory.CreateClient();

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<LocalJwtService>();
            token = jwt.GenerateToken(77, "real@example.test", "Real User", new[] { "OnCall.Viewer" });
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body!["authMode"].ToString().Should().Be("production");
    }

    private static string Flatten(Exception ex)
    {
        var text = ex.Message;
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            text += " | " + inner.Message;
        return text;
    }
}
