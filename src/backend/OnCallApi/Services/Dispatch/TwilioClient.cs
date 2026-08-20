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
            var fields = new Dictionary<string, string>
            {
                ["To"] = toPhone,
                ["Body"] = message,
            };

            // A messaging service carries the A2P 10DLC campaign registration; when one is
            // configured it replaces the individual sender number.
            if (_options.UseMessagingService)
                fields["MessagingServiceSid"] = _options.MessagingServiceSid;
            else
                fields["From"] = _options.FromNumber;

            // Without a status callback, Twilio's 201 ("queued") is the only signal we get,
            // and an undelivered code-call alert would look like a success.
            if (!string.IsNullOrWhiteSpace(_options.StatusCallbackUrl))
                fields["StatusCallback"] = _options.StatusCallbackUrl;

            var response = await _httpClient.PostAsync(
                $"Accounts/{_options.AccountSid}/Messages.json", new FormUrlEncodedContent(fields));
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // The recipient number is PHI-adjacent directory data — log the failure,
                // never the number.
                _logger.LogWarning("Twilio SMS failed ({Code}): {Body}", (int)response.StatusCode, body);
                return new DispatchResult
                {
                    Success = false,
                    ErrorMessage = $"Twilio error {(int)response.StatusCode}",
                    Detail = ExtractTwilioError(body) ?? $"Twilio error {(int)response.StatusCode}",
                };
            }

            // Extract the Message SID (for webhook correlation + the audit trail) and the
            // initial status, which is "queued"/"accepted" — NOT proof of delivery.
            var (sid, status) = ParseMessageResponse(body);

            _logger.LogInformation("Twilio SMS accepted (SID {Sid}, status {Status})",
                sid ?? "n/a", status ?? "unknown");

            return new DispatchResult
            {
                Success = true,
                IncidentId = sid ?? "",
                Detail = string.IsNullOrWhiteSpace(_options.StatusCallbackUrl)
                    ? $"SMS {status ?? "queued"} (no delivery confirmation configured)"
                    : $"SMS {status ?? "queued"} — awaiting delivery confirmation",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Twilio SMS send failed");
            return new DispatchResult { Success = false, ErrorMessage = ex.Message, Detail = ex.Message };
        }
    }

    private static (string? Sid, string? Status) ParseMessageResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var sid = doc.RootElement.TryGetProperty("sid", out var sidProp) ? sidProp.GetString() : null;
            var status = doc.RootElement.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
            return (sid, status);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Twilio error bodies carry a human-readable "message" and numeric "code".</summary>
    private static string? ExtractTwilioError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            var code = doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var codeVal)
                ? codeVal.ToString()
                : null;
            if (msg == null) return null;
            return code == null ? msg : $"{msg} (Twilio code {code})";
        }
        catch (JsonException)
        {
            return null;
        }
    }
}