namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Client for Stryker Vocera Messaging Platform (VMP) API.
/// Sends alerts to Vocera badges and queries device status.
/// Uses SOAP for the VMP API and REST for the Clinical API.
/// </summary>
public interface IVoceraClient
{
    /// <summary>Verify VMP API connectivity.</summary>
    Task<ConnectionStatus> CheckConnectionAsync();

    /// <summary>Send a text alert to a Vocera badge.</summary>
    Task<VoceraMessageResult> SendAlertAsync(string badgeId, string message, VoceraPriority priority = VoceraPriority.Critical);

    /// <summary>Cancel a pending alert by event ID.</summary>
    Task<bool> CancelAlertAsync(string eventId);

    /// <summary>Check if a badge is online and reachable.</summary>
    Task<bool> GetDeviceStatusAsync(string badgeId);
}
