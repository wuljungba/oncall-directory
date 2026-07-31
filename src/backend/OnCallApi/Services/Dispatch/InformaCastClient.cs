using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;

namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Real implementation of the InformaCast Fusion REST API client.
/// Sends emergency alerts and triggers scenarios via the InformaCast API.
/// </summary>
public class InformaCastClient : IInformaCastClient
{
    private readonly HttpClient _httpClient;
    private readonly InformaCastOptions _options;
    private readonly ILogger<InformaCastClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public InformaCastClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DispatchOptions> options,
        ILogger<InformaCastClient> logger)
    {
        _options = options.Value.InformaCast;
        _logger = logger;

        _httpClient = httpClientFactory.CreateClient("InformaCast");
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds);
        _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/'));
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ConnectionStatus> CheckConnectionAsync()
    {
        try
        {
            // Try listing recipients as a connectivity test
            var response = await _httpClient.GetAsync("/api/v1/recipients?limit=1");
            if (response.IsSuccessStatusCode)
            {
                return new ConnectionStatus
                {
                    Connected = true,
                    Detail = $"API reachable, token valid ({response.StatusCode})",
                    LastCheckedAt = DateTime.UtcNow,
                };
            }

            return new ConnectionStatus
            {
                Connected = false,
                Detail = $"API returned {response.StatusCode}",
                LastCheckedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InformaCast connection check failed");
            return new ConnectionStatus
            {
                Connected = false,
                Detail = ex.Message,
                LastCheckedAt = DateTime.UtcNow,
            };
        }
    }

    public async Task<InformaCastResult> TriggerScenarioAsync(string scenarioId, string location, string message)
    {
        try
        {
            var payload = new
            {
                source = "OnCall Scheduler",
                priority = "CRITICAL",
                message,
                locations = new[] { location },
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"/api/v1/scenarios/{scenarioId}/trigger", content);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogInformation(
                    "InformaCast scenario triggered: {ScenarioId} at {Location}, response={Response}",
                    scenarioId, location, responseBody);

                return new InformaCastResult
                {
                    Success = true,
                    IncidentId = $"{scenarioId}-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    Detail = "Scenario triggered successfully",
                };
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning(
                "InformaCast scenario trigger failed: {StatusCode}, {Error}",
                response.StatusCode, errorBody);

            return new InformaCastResult
            {
                Success = false,
                Detail = $"Trigger failed: {response.StatusCode} - {errorBody}",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InformaCast scenario trigger failed");
            return new InformaCastResult
            {
                Success = false,
                Detail = ex.Message,
            };
        }
    }

    public async Task<InformaCastResult> SendAlertAsync(string recipientGroup, string message, string priority = "CRITICAL")
    {
        try
        {
            var payload = new
            {
                source = "OnCall Scheduler",
                priority,
                message,
                recipients = new[] { recipientGroup },
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/v1/notifications/send", content);

            if (response.IsSuccessStatusCode)
            {
                return new InformaCastResult
                {
                    Success = true,
                    IncidentId = $"alert-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    Detail = $"Alert sent to {recipientGroup}",
                };
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            return new InformaCastResult
            {
                Success = false,
                Detail = $"Alert failed: {response.StatusCode} - {errorBody}",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InformaCast send alert failed");
            return new InformaCastResult
            {
                Success = false,
                Detail = ex.Message,
            };
        }
    }

    public async Task<string> GetScenarioStatusAsync(string incidentId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/v1/incidents/{incidentId}");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return body;
            }
            return $"Unknown (HTTP {response.StatusCode})";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InformaCast status check failed for {IncidentId}", incidentId);
            return $"Error: {ex.Message}";
        }
    }
}
