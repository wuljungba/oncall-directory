using OnCallApi.Services.Dispatch;

namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Client for Cisco Unified Communications Manager (CUCM) AXL API.
/// Uses SOAP/XML over HTTPS for administrative operations and paging.
/// </summary>
public interface ICiscoCucmClient
{
    /// <summary>Check AXL connectivity and return device registration status.</summary>
    Task<ConnectionStatus> CheckConnectionAsync();

    /// <summary>Check if specific paging devices at a location are registered.</summary>
    Task<bool> CheckDeviceRegistrationAsync(string location);

    /// <summary>Initiate a page through CUCM (SIP trunk or CTI route point).</summary>
    Task<CucmPageResult> InitiatePageAsync(string callingParty, string dialedNumber, string location);

    /// <summary>Get the count of registered devices.</summary>
    Task<int> GetRegisteredDeviceCountAsync();
}
