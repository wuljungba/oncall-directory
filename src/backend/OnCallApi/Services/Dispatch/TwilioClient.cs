using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;

namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Twilio Programmable Messaging (SMS) client. Sends a code-call alert as a text message to
/// a provider's mobile number using Twilio's Messages REST API, authenticated with the
/// Account SID + Auth Token via HTTP Basic auth.
/// </summary>
public class TwilioClient : ITwilioClient
{
    private const string ApiBase = "https://api.twilio.com/2010-04-01";
    private readonly HttpClient _httpClient;
    private readonly TwilioOptions _options;
    private readonly ILogger<TwilioClient> _logger;

    public TwilioClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DispatchOptions> options,
        ILogger<TwilioClient> logger)
    {
        _options = options.Value.Twilio;
        _logger = logger;

        _httpClient = httpClientFactory.CreateClient("Twilio");
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds);
        _httpClient.BaseAddress = new Uri(ApiBase);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.AccountSid}:{_options.AuthToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    public async Task<ConnectionStatus> CheckConnectionAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"Accounts/{_options.AccountSid}.json");
            var detail = response.IsSuccessStatusCode
                ? "Twilio account reachable"
                : $"Twilio account check failed ({(int)response.StatusCode})";
            return new ConnectionStatus { Connected = response.IsSuccessStatusCode, Detail = detail };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twilio connection check failed");
            return new ConnectionStatus { Connected = false, Detail = ex.Message };
        }
    }

    public async Task<DispatchResult> SendSmsAsync(string toPhone, string message)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = toPhone,
                ["From"] = _options.FromNumber,
                ["Body"] = message,
            });

            var response = await _httpClient.PostAsync($"Accounts/{_options.AccountSid}/Messages.json", form);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Twilio SMS failed ({Code}) for {To}: {Body}",
                    (int)response.StatusCode, toPhone, body);
                return new DispatchResult { Success = false, ErrorMessage = $"Twilio error {(int)response.StatusCode}" };
            }

            // Extract the Message SID from the response for the audit trail.
            string? sid = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("sid", out var sidProp))
                    sid = sidProp.GetString();
            }
            catch { /* ignore parse */ }

            _logger.LogInformation("Twilio SMS sent to {To} (SID {Sid})", toPhone, sid ?? "n/a");
            return new DispatchResult { Success = true, IncidentId = sid ?? "", Detail = "SMS delivered" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twilio SMS send failed for {To}", toPhone);
            return new DispatchResult { Success = false, ErrorMessage = ex.Message };
        }
    }
}