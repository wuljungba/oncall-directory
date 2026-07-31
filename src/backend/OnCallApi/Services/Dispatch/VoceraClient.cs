using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;

namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Real implementation of the Stryker Vocera Messaging Platform (VMP) SOAP client.
/// Sends emergency alerts to Vocera wearable badges and checks device status.
/// </summary>
public class VoceraClient : IVoceraClient
{
    private readonly HttpClient _httpClient;
    private readonly VoceraOptions _options;
    private readonly ILogger<VoceraClient> _logger;

    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace VmpNs = "http://www.vocera.com/VMPWebServices";

    public VoceraClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DispatchOptions> options,
        ILogger<VoceraClient> logger)
    {
        _options = options.Value.Vocera;
        _logger = logger;

        _httpClient = httpClientFactory.CreateClient("Vocera");
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds);
        _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/'));
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _options.ApiKey);
    }

    public async Task<ConnectionStatus> CheckConnectionAsync()
    {
        try
        {
            // Try the REST API version endpoint
            var response = await _httpClient.GetAsync("/api/v1/version");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return new ConnectionStatus
                {
                    Connected = true,
                    Detail = $"Vocera reachable: {body.Truncate(100)}",
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
            _logger.LogWarning(ex, "Vocera connection check failed");
            return new ConnectionStatus
            {
                Connected = false,
                Detail = ex.Message,
                LastCheckedAt = DateTime.UtcNow,
            };
        }
    }

    public async Task<VoceraMessageResult> SendAlertAsync(string badgeId, string message, VoceraPriority priority = VoceraPriority.Critical)
    {
        try
        {
            // Build the SOAP envelope for Paging_SendAlert
            var soapEnvelope = new XDocument(
                new XElement(SoapNs + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "soap", SoapNs.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "vmp", VmpNs.NamespaceName),
                    new XElement(SoapNs + "Body",
                        new XElement(VmpNs + "Paging_SendAlert",
                            new XElement("strSender", "OnCall System"),
                            new XElement("strRecipientBadgeId", badgeId),
                            new XElement("strMessage", message),
                            new XElement("iPriority", (int)priority)))));

            var content = new StringContent(
                soapEnvelope.ToString(), Encoding.UTF8, "text/xml; charset=utf-8");

            var response = await _httpClient.PostAsync("/VMPWebServices/VMPWebService.asmx", content);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogInformation(
                    "Vocera alert sent to badge {BadgeId}: priority={Priority}, message={Message}",
                    badgeId, priority, message.Truncate(50));

                return new VoceraMessageResult
                {
                    Success = true,
                    MessageId = $"voc-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    Detail = "Alert sent to Vocera badge successfully",
                };
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning(
                "Vocera alert failed for badge {BadgeId}: {StatusCode}, {Error}",
                badgeId, response.StatusCode, errorBody.Truncate(200));

            return new VoceraMessageResult
            {
                Success = false,
                Detail = $"Vocera API error: {response.StatusCode}",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vocera send alert failed for badge {BadgeId}", badgeId);
            return new VoceraMessageResult
            {
                Success = false,
                Detail = ex.Message,
            };
        }
    }

    public async Task<bool> CancelAlertAsync(string eventId)
    {
        try
        {
            var soapEnvelope = new XDocument(
                new XElement(SoapNs + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "soap", SoapNs.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "vmp", VmpNs.NamespaceName),
                    new XElement(SoapNs + "Body",
                        new XElement(VmpNs + "Paging_CancelAlertByEventID",
                            new XElement("strEventID", eventId)))));

            var content = new StringContent(
                soapEnvelope.ToString(), Encoding.UTF8, "text/xml; charset=utf-8");

            var response = await _httpClient.PostAsync("/VMPWebServices/VMPWebService.asmx", content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vocera cancel alert failed for event {EventId}", eventId);
            return false;
        }
    }

    public async Task<bool> GetDeviceStatusAsync(string badgeId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/v1/devices/{badgeId}/status");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vocera device status check failed for badge {BadgeId}", badgeId);
            return false;
        }
    }
}

/// <summary>
/// Extension helper for truncating strings in log output.
/// </summary>
internal static class StringExtensions
{
    public static string Truncate(this string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
