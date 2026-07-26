using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Orchestrates the dispatch of code call alerts through external paging channels.
/// In production, this integrates with InformaCast, Vocera, and Cisco CUCM.
/// </summary>
public interface ICodeCallDispatchService
{
    /// <summary>
    /// Runs the full dispatch pipeline for an incident asynchronously.
    /// Creates dispatch steps and broadcasts SignalR events as each step completes.
    /// </summary>
    Task DispatchIncidentAsync(PhoneTreeEvent evt, string codeType);

    /// <summary>
    /// Simulates a CUCM AXL pre-flight check for paging devices at a location.
    /// </summary>
    Task<bool> PreflightCheckDevicesAsync(string location);
}
