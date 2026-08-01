using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Authentication;

namespace BackendTests.Services;

/// <summary>
/// Guards the production fail-fast behavior for the local JWT signing key
/// (code-review Finding 1): a weak or missing key must never sign tokens in
/// production. Dev environments may use the well-known fallback.
/// </summary>
public class LocalJwtServiceTests
{
    private static LocalJwtService CreateService(string environment, string? signingKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Local:SigningKey"] = signingKey,
            })
            .Build();

        var hostEnv = new StubHostEnvironment { EnvironmentName = environment };

        return new LocalJwtService(config, NullLogger<LocalJwtService>.Instance, hostEnv);
    }

    [Theory]
    [InlineData(null)]                       // key entirely absent
    [InlineData("short")]                    // key present but < 32 chars
    public void GenerateToken_Production_ThrowsOnWeakKey(string? signingKey)
    {
        var service = CreateService(Environments.Production, signingKey);

        var act = () => service.GenerateToken(1, "u@h.org", "User", ["OnCall.Viewer"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SigningKey*");
    }

    [Fact]
    public void GenerateToken_Production_SucceedsWithStrongKey()
    {
        var service = CreateService(
            Environments.Production,
            "a-real-32-char-min-secret-key-for-production!!");

        var token = service.GenerateToken(1, "u@h.org", "User", ["OnCall.Viewer"]);

        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateToken_Development_UsesFallbackKey()
    {
        // Missing key in a non-production environment must not throw — the
        // well-known dev fallback keeps local development friction-free.
        var service = CreateService(Environments.Development, signingKey: null);

        var token = service.GenerateToken(1, "u@h.org", "User", ["OnCall.Viewer"]);

        token.Should().NotBeNullOrEmpty();
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
