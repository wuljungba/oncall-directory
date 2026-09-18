using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;

namespace OnCallApi.Services;

/// <summary>
/// Hands out the Graph client for a directory.
///
/// This exists as a seam rather than as private methods on <see cref="GraphApiService"/> because
/// the delta paging loop lives below <see cref="IGraphApiService"/>: faking that interface proves
/// nothing about whether the stored delta link was actually sent, or whether every page was
/// followed. Those two are the whole point of the delta rework, so they need a place where a test
/// can supply a client backed by a stub message handler.
/// </summary>
public interface IGraphClientFactory
{
    /// <summary>
    /// A client for one Entra directory. Blank, or our own tenant id, means the home directory.
    /// </summary>
    GraphServiceClient For(string? entraTenantId);
}

public class GraphClientFactory : IGraphClientFactory
{
    private readonly IOptions<GraphApiOptions> _options;
    private readonly ILogger<GraphClientFactory> _logger;
    private GraphServiceClient? _client;
    private bool _clientInitialized;

    // One client per connected customer directory. Each customer consents to this same
    // application in their own Entra tenant, which creates a service principal there; the
    // client id and secret are ours and constant, and only the tenant id varies. Cached
    // because building a credential per sync cycle would re-do the token dance every time.
    private readonly ConcurrentDictionary<string, GraphServiceClient> _tenantClients = new();

    public GraphClientFactory(IOptions<GraphApiOptions> options, ILogger<GraphClientFactory> logger)
    {
        _options = options;
        _logger = logger;
    }

    public GraphServiceClient For(string? entraTenantId)
    {
        if (string.IsNullOrWhiteSpace(entraTenantId)
            || string.Equals(entraTenantId, _options.Value.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return Home();
        }

        return _tenantClients.GetOrAdd(entraTenantId, tenantId =>
        {
            try
            {
                var creds = new ClientSecretCredential(
                    tenantId, _options.Value.ClientId, _options.Value.ClientSecret);
                _logger.LogInformation("GraphServiceClient initialized for connected directory {TenantId}", tenantId);
                return new GraphServiceClient(creds, _options.Value.Scopes);
            }
            catch (Exception ex)
            {
                // Do not cache a broken client: GetOrAdd would keep handing it back and the
                // customer's directory would stay dead until a restart.
                _logger.LogError(ex, "Failed to initialize GraphServiceClient for directory {TenantId}", tenantId);
                throw;
            }
        });
    }

    private GraphServiceClient Home()
    {
        if (_client != null) return _client;
        if (_clientInitialized)
        {
            throw new InvalidOperationException("Graph API client initialization already failed; check previous logs.");
        }
        _clientInitialized = true;

        try
        {
            var creds = new ClientSecretCredential(
                _options.Value.TenantId,
                _options.Value.ClientId,
                _options.Value.ClientSecret);
            _client = new GraphServiceClient(creds, _options.Value.Scopes);
            _logger.LogInformation("GraphServiceClient initialized successfully with {ScopeCount} scope(s)",
                _options.Value.Scopes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize GraphServiceClient (tenant: {TenantId})",
                _options.Value.TenantId);
            throw;
        }
        return _client;
    }
}
