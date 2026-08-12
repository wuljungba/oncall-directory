namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Client for Twilio Programmable Messaging (SMS). Delivers code-call text alerts to a
/// provider's mobile phone. Uses Twilio's Messages REST API with Account SID + Auth Token.
/// </summary>
public interface ITwilioClient
{
    /// <summary>Check connectivity to the Twilio account (read-only account fetch).</summary>
    Task<ConnectionStatus> CheckConnectionAsync();

    /// <summary>Send an SMS. <paramref name="toPhone"/> is E.164 (e.g. +12025551234).</summary>
    Task<DispatchResult> SendSmsAsync(string toPhone, string message);
}