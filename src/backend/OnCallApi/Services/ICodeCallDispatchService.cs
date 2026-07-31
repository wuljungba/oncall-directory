using OnCallApi.Services.Dispatch;

namespace OnCallApi.Services;

/// <summary>
/// Orchestrates the dispatch of code call alerts through external paging channels.
/// Integrates with InformaCast, Vocera, and Cisco CUCM for real emergency alerts.
/// </summary>
public interface ICodeCallDispatchService
{
    /// <summary>
    /// Enqueues an incident for dispatch processing and returns immediately.
    /// The job is processed asynchronously by <see cref="DispatchBackgroundService"/>.
    /// </summary>
    Task DispatchIncidentAsync(int eventId, string codeType);

    /// <summary>
    /// Runs the full dispatch pipeline for a single queued job.
    /// Creates dispatch steps and broadcasts SignalR events as each step completes.
    /// Called by <see cref="DispatchBackgroundService"/>; not intended for direct callers.
    /// </summary>
    Task ProcessDispatchJobAsync(int eventId, string codeType);

    /// <summary>
    /// Performs a pre-flight check of all configured dispatch channels.
    /// Returns a list of connection statuses for each channel.
    /// </summary>
    Task<List<ConnectionStatus>> PreflightCheckAllAsync();

    /// <summary>
    /// Performs a pre-flight check for paging devices at a specific location.
    /// </summary>
    Task<bool> PreflightCheckDevicesAsync(string location);
}
